﻿using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using StableUI.Accounts;
using StableUI.Backends;
using StableUI.Core;
using StableUI.DataHolders;
using StableUI.Text2Image;
using StableUI.Utils;
using System.IO;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace StableUI.WebAPI;

/// <summary>Text-to-Image API routes</summary>
public static class T2IAPI
{
    public static void Register()
    {
        API.RegisterAPICall(GenerateText2Image);
        API.RegisterAPICall(GenerateText2ImageWS);
        API.RegisterAPICall(ListImages);
        API.RegisterAPICall(ListModels);
        API.RegisterAPICall(ListLoadedModels);
        API.RegisterAPICall(TriggerRefresh);
        API.RegisterAPICall(SelectModel);
        API.RegisterAPICall(SelectModelWS);
        API.RegisterAPICall(ListT2IParams);
    }

    /// <summary>API route to generate images with WebSocket updates.</summary>
    public static async Task<JObject> GenerateText2ImageWS(WebSocket socket, Session session, int images, JObject rawInput)
    {
        await API.RunWebsocketHandlerCallWS(GenT2I_Internal, session, (images, rawInput), socket);
        await socket.SendJson(BasicAPIFeatures.GetCurrentStatusRaw(session), API.WebsocketTimeout);
        return null;
    }

    /// <summary>API route to generate images directly as HTTP.</summary>
    public static async Task<JObject> GenerateText2Image(Session session, int images, JObject rawInput)
    {
        List<JObject> outputs = await API.RunWebsocketHandlerCallDirect(GenT2I_Internal, session, (images, rawInput));
        List<string> imageOutputs = new();
        foreach (JObject obj in outputs)
        {
            if (obj.ContainsKey("error"))
            {
                return obj;
            }
            if (obj.ContainsKey("image"))
            {
                imageOutputs.Add(obj["image"].ToString());
            }
        }
        return new JObject() { ["images"] = JToken.FromObject(imageOutputs) };
    }

    /// <summary>Internal route for generating images.</summary>
    public static async Task GenT2I_Internal(Session session, (int, JObject) input, Action<JObject> output, bool isWS)
    {
        (int images, JObject rawInput) = input;
        using Session.GenClaim claim = session.Claim(gens: images);
        T2IParams user_input = new(session);
        try
        {
            foreach ((string key, JToken val) in rawInput)
            {
                if (T2IParamTypes.Types.ContainsKey(T2IParamTypes.CleanTypeName(key)))
                {
                    T2IParamTypes.ApplyParameter(key, val.ToString(), user_input);
                }
            }
            if (rawInput.TryGetValue("presets", out JToken presets))
            {
                foreach (JToken presetName in presets.Values())
                {
                    T2IPreset presetObj = session.User.GetPreset(presetName.ToString());
                    presetObj.ApplyTo(user_input);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            output(new JObject() { ["error"] = ex.Message });
            return;
        }
        if (user_input.Seed == -1)
        {
            user_input.Seed = Random.Shared.Next(int.MaxValue);
        }
        void setError(string message)
        {
            output(new JObject() { ["error"] = message });
            claim.LocalClaimInterrupt.Cancel();
        }
        List<Task> tasks = new();
        int max_degrees = session.User.Settings.MaxT2ISimultaneous;
        for (int i = 0; i < images && !claim.ShouldCancel; i++)
        {
            tasks.RemoveAll(t => t.IsCompleted);
            while (tasks.Count > max_degrees)
            {
                await Task.WhenAny(tasks);
            }
            if (claim.ShouldCancel)
            {
                break;
            }
            int index = i;
            T2IParams thisParams = user_input.Clone();
            thisParams.Seed += index;
            tasks.Add(Task.Run(() => CreateImageTask(thisParams, claim, output, setError, isWS, 2, // TODO: Max timespan configurable
                (outputs) =>
                {
                    foreach (Image image in outputs)
                    {
                        string url = session.SaveImage(image, user_input);
                        if (url == "ERROR")
                        {
                            setError($"Server failed to save images.");
                            return;
                        }
                        output(new JObject() { ["image"] = url });
                    }
                })));
        }
        while (tasks.Any())
        {
            await Task.WhenAny(tasks);
            tasks.RemoveAll(t => t.IsCompleted);
        }
    }

    /// <summary>Internal handler route to create an image based on a user request.</summary>
    public static async Task CreateImageTask(T2IParams user_input, Session.GenClaim claim, Action<JObject> output, Action<string> setError, bool isWS, float backendTimeoutMin, Action<Image[]> saveImages)
    {
        void sendStatus()
        {
            if (isWS && user_input.SourceSession is not null)
            {
                output(BasicAPIFeatures.GetCurrentStatusRaw(user_input.SourceSession));
            }
        }
        if (claim.ShouldCancel)
        {
            return;
        }
        T2IBackendAccess backend;
        int modelLoads = 0;
        try
        {
            claim.Extend(backendWaits: 1);
            sendStatus();
            backend = await Program.Backends.GetNextT2IBackend(TimeSpan.FromMinutes(backendTimeoutMin), user_input.Model, filter: user_input.BackendMatcher, session: user_input.SourceSession, notifyWillLoad: () =>
            {
                modelLoads++;
                claim.Extend(modelLoads: 1);
                sendStatus();
            }, cancel: claim.InterruptToken);
        }
        catch (InvalidOperationException ex)
        {
            setError($"Invalid operation: {ex.Message}");
            return;
        }
        catch (TimeoutException)
        {
            setError("Timeout! All backends are occupied with other tasks.");
            return;
        }
        finally
        {
            claim.Complete(modelLoads: modelLoads, backendWaits: 1);
            sendStatus();
        }
        if (claim.ShouldCancel)
        {
            backend.Dispose();
            return;
        }
        try
        {
            claim.Extend(liveGens: 1);
            sendStatus();
            using (backend)
            {
                if (claim.ShouldCancel)
                {
                    return;
                }
                Image[] outputs = await backend.Backend.Generate(user_input);
                saveImages(outputs);
            }
        }
        catch (InvalidDataException ex)
        {
            setError($"Invalid data: {ex.Message}");
            return;
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logs.Error($"Internal error processing T2I request: {ex}");
            setError("Something went wrong while generating images.");
            return;
        }
        finally
        {
            claim.Complete(gens: 1, liveGens: 1);
            sendStatus();
        }
    }

    public static HashSet<string> ImageExtensions = new() { "png", "jpg" };

    /// <summary>API route to get a list of available history images.</summary>
    private static JObject GetListAPIInternal(Session session, string path, string root, HashSet<string> extensions, Func<string, bool> isAllowed, Func<string, string, JObject> valToObj)
    {
        (path, string consoleError, string userError) = WebServer.CheckFilePath(root, path);
        if (consoleError is not null)
        {
            Logs.Error(consoleError);
            return new JObject() { ["error"] = userError };
        }
        try
        {
            return new JObject()
            {
                ["folders"] = JToken.FromObject(Directory.EnumerateDirectories(path).Select(Path.GetFileName).Where(isAllowed).ToList()),
                ["files"] = JToken.FromObject(Directory.EnumerateFiles(path).Where(isAllowed).Where(f => extensions.Contains(f.AfterLast('.'))).Select(f => f.Replace('\\', '/')).Select(f => valToObj(f, f.AfterLast('/'))).ToList())
            };
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is PathTooLongException)
            {
                return new JObject() { ["error"] = "404, path not found." };
            }
            else
            {
                return new JObject() { ["error"] = "Error reading file list." };
            }
        }
    }

    /// <summary>API route to get a list of available history images.</summary>
    public static async Task<JObject> ListImages(Session session, string path)
    {
        string root = $"{Environment.CurrentDirectory}/{Program.ServerSettings.OutputPath}/{session.User.UserID}";
        return GetListAPIInternal(session, path, root, ImageExtensions, f => true, (file, name) => new JObject() { ["src"] = name, ["batch_id"] = 0 });
    }

    public static HashSet<string> ModelExtensions = new() { "safetensors", "ckpt" };

    /// <summary>API route to get a list of available models.</summary>
    public static async Task<JObject> ListModels(Session session, string path)
    {
        if (path != "")
        {
            path += '/';
        }
        string allowedStr = session.User.Restrictions.AllowedModels;
        Regex allowed = allowedStr == ".*" ? null : new Regex(allowedStr, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        List<T2IModel> matches = Program.T2IModels.Models.Values.Where(m => m.Name.StartsWith(path) && m.Name.Length > path.Length && (allowed is null || allowed.IsMatch(m.Name))).ToList();
        return new JObject()
        {
            ["folders"] = JToken.FromObject(matches.Where(m => m.Name[path.Length..].Contains('/')).Select(m => m.Name.BeforeLast('/').AfterLast('/')).Distinct().ToList()),
            ["files"] = JToken.FromObject(matches.Where(m => !m.Name[path.Length..].Contains('/')).Select(m => m.ToNetObject()).ToList())
        };
    }

    /// <summary>API route to get a list of currently loaded models.</summary>
    public static async Task<JObject> ListLoadedModels(Session session)
    {
        string allowedStr = session.User.Restrictions.AllowedModels;
        Regex allowed = allowedStr == ".*" ? null : new Regex(allowedStr, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        List<T2IModel> matches = Program.T2IModels.Models.Values.Where(m => m.AnyBackendsHaveLoaded && (allowed is null || allowed.IsMatch(m.Name))).ToList();
        return new JObject()
        {
            ["models"] = JToken.FromObject(matches.Select(m => m.ToNetObject()).ToList())
        };
    }

    /// <summary>API route to trigger a reload of the model list.</summary>
    public static async Task<JObject> TriggerRefresh(Session session)
    {
        Program.ModelRefreshEvent?.Invoke();
        return await ListT2IParams(session);
    }

    /// <summary>API route to select a model for loading.</summary>
    public static async Task<JObject> SelectModel(Session session, string model)
    {
        return (await API.RunWebsocketHandlerCallDirect(SelectModelInternal, session, model))[0];
    }

    /// <summary>API route to select a model for loading, as a websocket with live status updates.</summary>
    public static async Task<JObject> SelectModelWS(WebSocket socket, Session session, string model)
    {
        await API.RunWebsocketHandlerCallWS(SelectModelInternal, session, model, socket);
        await socket.SendJson(BasicAPIFeatures.GetCurrentStatusRaw(session), API.WebsocketTimeout);
        return null;
    }

    /// <summary>Internal handler of the model-load API route.</summary>
    public static async Task SelectModelInternal(Session session, string model, Action<JObject> output, bool isWS)
    {
        if (!session.User.Restrictions.CanChangeModels)
        {
            output(new JObject() { ["error"] = "You are not allowed to change models." });
            return;
        }
        string allowedStr = session.User.Restrictions.AllowedModels;
        Regex allowed = allowedStr == ".*" ? null : new Regex(allowedStr, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        if (allowed != null && !allowed.IsMatch(model) || !Program.T2IModels.Models.TryGetValue(model, out T2IModel actualModel))
        {
            output(new JObject() { ["error"] = "Model not found." });
            return;
        }
        using Session.GenClaim claim = session.Claim(0, Program.Backends.T2IBackends.Count, 0, 0);
        if (isWS)
        {
            output(BasicAPIFeatures.GetCurrentStatusRaw(session));
        }
        if (!(await Program.Backends.LoadModelOnAll(actualModel)))
        {
            output(new JObject() { ["error"] = "Model failed to load." });
            return;
        }
        output(new JObject() { ["success"] = true });
    }

    /// <summary>API route to get a list of parameter types.</summary>
    public static async Task<JObject> ListT2IParams(Session session)
    {
        return new JObject()
        {
            ["list"] = JToken.FromObject(T2IParamTypes.Types.Values.Select(v => v.ToNet(session)).ToList())
        };
    }
}
