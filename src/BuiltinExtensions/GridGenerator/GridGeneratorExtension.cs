﻿using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using StableUI.Accounts;
using StableUI.Backends;
using StableUI.Core;
using StableUI.DataHolders;
using StableUI.Text2Image;
using StableUI.Utils;
using StableUI.WebAPI;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace StableUI.Builtin_GridGeneratorExtension;

/// <summary>Extension that adds a tool to generate grids of images.</summary>
public class GridGeneratorExtension : Extension
{
    public override void OnPreInit()
    {
        ScriptFiles.Add("Assets/grid_gen.js");
        StyleSheetFiles.Add("Assets/grid_gen.css");
        GridGenCore.ASSETS_DIR = $"{FilePath}/Assets";
        GridGenCore.EXTRA_FOOTER = $"Images area auto-generated by an AI (Stable Diffusion) and so may not have been reviewed by the page author before publishing.\n<script src=\"stableui_gridgen_local.js?vary={Utilities.VaryID}\"></script>";
        GridGenCore.EXTRA_ASSETS.Add("stableui_gridgen_local.js");
        T2IParamTypes.Register(new("[Grid Gen] Prompt Replace", "Replace text in the prompt (or negative prompt) with some other text.", T2IParamDataType.TEXT, "", (s, p) => throw new Exception("Prompt replace mishandled!"), VisibleNormally: false, Toggleable: true));
        GridGenCore.GridCallInitHook = (call) =>
        {
            call.LocalData = new GridCallData();
        };
        GridGenCore.GridCallParamAddHook = (call, param, val) =>
        {
            if (call.Grid.MinWidth == 0)
            {
                call.Grid.MinWidth = call.Grid.InitialParams.Width;
            }
            if (call.Grid.MinHeight == 0)
            {
                call.Grid.MinHeight = call.Grid.InitialParams.Height;
            }
            string cleaned = T2IParamTypes.CleanTypeName(param);
            if (cleaned == "promptreplace")
            {
                (call.LocalData as GridCallData).Replacements.Add(val);
                return true;
            }
            else if (cleaned == "width" || cleaned == "outwidth")
            {
                call.Grid.MinWidth = Math.Min(call.Grid.MinWidth, int.Parse(val));
            }
            else if (cleaned == "height" || cleaned == "outheight")
            {
                call.Grid.MinHeight = Math.Min(call.Grid.MinHeight, int.Parse(val));
            }
            return false;
        };
        GridGenCore.GridCallApplyHook = (call, param, dry) =>
        {
            foreach (string replacement in (call.LocalData as GridCallData).Replacements)
            {
                string[] parts = replacement.Split('=', 2);
                string key = parts[0].Trim();
                string val = parts[1].Trim();
                param.Prompt = param.Prompt.Replace(key, val);
                param.NegativePrompt = param.NegativePrompt.Replace(key, val);
                foreach (string paramId in param.OtherParams.Keys.Where(k => k.EndsWith("_prompt") && param.OtherParams[key] is string).ToArray())
                {
                    param.OtherParams[paramId] = param.OtherParams[paramId].ToString().Replace(key, val);
                }
            }
        };
        GridGenCore.GridRunnerPreRunHook = (runner) =>
        {
            // TODO: Progress update
        };
        GridGenCore.GridRunnerPreDryHook = (runner) =>
        {
            // Nothing to do.
        };
        GridGenCore.GridRunnerPostDryHook = (runner, param, set) =>
        {
            if (param.Seed == -1)
            {
                param.Seed = Random.Shared.Next();
            }
            if (param.VarSeed == -1)
            {
                param.VarSeed = Random.Shared.Next();
            }
            StableUIGridData data = runner.Grid.LocalData as StableUIGridData;
            Task[] waitOn = data.GetActive();
            if (waitOn.Length > data.Session.User.Settings.MaxT2ISimultaneous)
            {
                Task.WaitAny(waitOn);
            }
            if (Volatile.Read(ref data.ErrorOut) is not null)
            {
                throw new InvalidOperationException("Errored");
            }
            T2IParams thisParams = param.Clone();
            Task t = Task.Run(async () =>
            {
                T2IBackendAccess backend;
                try
                {
                    T2IExtra extra = thisParams.ExternalData as T2IExtra;
                    string backType = extra.BackendTypeMatcher?.ToLowerFast();
                    Func<BackendHandler.T2IBackendData, bool> filter = backType is null ? null : (d) => d.Backend.HandlerTypeData.ID.ToLowerFast() == backType;
                    backend = Program.Backends.GetNextT2IBackend(TimeSpan.FromMinutes(10), extra.Model, filter); // TODO: Max timespan configurable
                }
                catch (InvalidOperationException ex)
                {
                    Volatile.Write(ref data.ErrorOut, new JObject() { ["error"] = $"Invalid operation: {ex.Message}" });
                    return;
                }
                catch (TimeoutException)
                {
                    Volatile.Write(ref data.ErrorOut, new JObject() { ["error"] = "Timeout! All backends are occupied with other tasks." });
                    return;
                }
                Image[] outputs;
                using (backend)
                {
                    if (Volatile.Read(ref data.ErrorOut) is not null)
                    {
                        return;
                    }
                    outputs = await backend.Backend.Generate(thisParams);
                }
                if (outputs.Length != 1)
                {
                    Volatile.Write(ref data.ErrorOut, new JObject() { ["error"] = $"Server generated {outputs.Length} images when only expecting 1." });
                    return;
                }
                try
                {
                    string targetPath = $"{set.Grid.Runner.BasePath}/{set.BaseFilepath}.{set.Grid.Format}";
                    string dir = targetPath.Replace('\\', '/').BeforeLast('/');
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllBytes(targetPath, outputs[0].ImageData);
                    data.Generated.Enqueue($"/{set.Grid.Runner.URLBase}/{set.BaseFilepath}.{set.Grid.Format}");
                }
                catch (Exception ex)
                {
                    Logs.Error($"Grid gen failed to save image: {ex}");
                    Volatile.Write(ref data.ErrorOut, new JObject() { ["error"] = "Server failed to save image to file." });
                    return;
                }
            });
            lock (data.UpdateLock)
            {
                data.Rendering.Add(t);
            }
            return t;
        };
    }

    public override void OnInit()
    {
        API.RegisterAPICall(GridGenRun);
    }

    public class GridCallData
    {
        public List<string> Replacements = new();
    }

    public class T2IExtra : IDataHolder
    {
        public T2IModel Model;

        public string BackendTypeMatcher;

        public IDataHolder Clone()
        {
            return MemberwiseClone() as T2IExtra;
        }
    }

    public class StableUIGridData
    {
        public List<Task> Rendering = new();

        public LockObject UpdateLock = new();

        public ConcurrentQueue<string> Generated = new();

        public Session Session;

        public JObject ErrorOut;

        public Task[] GetActive()
        {
            lock (UpdateLock)
            {
                return Rendering.Where(x => !x.IsCompleted).ToArray();
            }
        }
    }

    public async Task<JObject> GridGenRun(WebSocket socket, Session session, T2IParams baseParams, JObject raw, string outputFolderName, bool doOverwrite, bool fastSkip, bool generatePage, bool publishGenMetadata, bool dryRun, string wanted_model = null)
    {
        if (baseParams.Seed == -1)
        {
            baseParams.Seed = Random.Shared.Next();
        }
        if (baseParams.VarSeed == -1)
        {
            baseParams.VarSeed = Random.Shared.Next();
        }
        T2IModel targetModel = null;
        if (wanted_model is not null && !Program.T2IModels.Models.TryGetValue(wanted_model, out targetModel))
        {
            await socket.SendJson(new JObject() { ["error"] = "Invalid model name" }, TimeSpan.FromMinutes(1));
            return null;
        }
        baseParams.ExternalData = new T2IExtra() { Model = targetModel };
        outputFolderName = Utilities.FilePathForbidden.TrimToNonMatches(outputFolderName);
        if (outputFolderName.Contains('.'))
        {
            await socket.SendJson(new JObject() { ["error"] = "Output folder name cannot contain dots." }, TimeSpan.FromMinutes(1));
            return null;
        }
        if (outputFolderName.Trim() == "")
        {
            await socket.SendJson(new JObject() { ["error"] = "Output folder name cannot be empty." }, TimeSpan.FromMinutes(1));
            return null;
        }
        StableUIGridData data = new() { Session = session };
        try
        {
            Task mainRun = Task.Run(() => GridGenCore.Run(baseParams, raw["gridAxes"], data, null, session.User.OutputDirectory, "Output", outputFolderName, doOverwrite, fastSkip, generatePage, publishGenMetadata, dryRun));
            while (!mainRun.IsCompleted || data.GetActive().Any())
            {
                await Utilities.WhenAny(Utilities.WhenAny(data.GetActive()), Task.Delay(TimeSpan.FromSeconds(2)));
                Program.GlobalProgramCancel.ThrowIfCancellationRequested();
                while (data.Generated.TryDequeue(out string nextImage))
                {
                    await socket.SendJson(new JObject() { ["image"] = nextImage }, TimeSpan.FromMinutes(1));
                }
            }
        }
        catch (InvalidDataException ex)
        {
            await socket.SendJson(new JObject() { ["error"] = $"Failed due to error: {ex.Message}" }, TimeSpan.FromMinutes(1));
            return null;
        }
        catch (Exception ex)
        {
            JObject err2 = Volatile.Read(ref data.ErrorOut);
            if (err2 is not null)
            {
                await socket.SendJson(new JObject() { ["error"] = err2 }, TimeSpan.FromMinutes(1));
                return null;
            }
            Logs.Error($"GridGen failed: {ex}");
            await socket.SendJson(new JObject() { ["error"] = "Failed due to internal error." }, TimeSpan.FromMinutes(1));
            return null;
        }
        JObject err = Volatile.Read(ref data.ErrorOut);
        if (err is not null)
        {
            await socket.SendJson(new JObject() { ["error"] = err }, TimeSpan.FromMinutes(1));
            return null;
        }
        while (data.Generated.TryDequeue(out string nextImage))
        {
            await socket.SendJson(new JObject() { ["image"] = nextImage }, TimeSpan.FromMinutes(1));
        }
        Logs.Info("Grid Generator completed successfully");
        string lastJsFile = $"{session.User.OutputDirectory}/{outputFolderName}/last.js";
        if (File.Exists(lastJsFile))
        {
            File.Delete(lastJsFile);
        }
        await socket.SendJson(new JObject() { ["success"] = "complete" }, TimeSpan.FromMinutes(1));
        return null;
    }
}
