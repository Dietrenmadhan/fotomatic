﻿
using Newtonsoft.Json.Linq;
using StableUI.DataHolders;
using System.IO;

namespace StableUI.Builtin_ComfyUIBackend;

/// <summary>Helper class for generating ComfyUI workflows from input parameters.</summary>
public class WorkflowGenerator
{
    /// <summary>Represents a step in the workflow generation process.</summary>
    /// <param name="Action">The action to take.</param>
    /// <param name="Priority">The priority to apply it at.
    /// These are such from lowest to highest.
    /// "-10" is the priority of the first core pre-init,
    /// "0" is before final outputs,
    /// "10" is final output.</param>
    public record class WorkflowGenStep(Action<WorkflowGenerator> Action, double Priority);

    /// <summary>Callable steps for modifying workflows as they go.</summary>
    public static List<WorkflowGenStep> Steps = new();

    /// <summary>Register a new step to the workflow generator.</summary>
    public static void AddStep(Action<WorkflowGenerator> step, double priority)
    {
        Steps.Add(new(step, priority));
        Steps = Steps.OrderBy(s => s.Priority).ToList();
    }

    static WorkflowGenerator()
    {
        AddStep(g =>
        {
            g.CreateNode("CheckpointLoaderSimple", (_, n) =>
            {
                n["inputs"] = new JObject()
                {
                    ["ckpt_name"] = g.UserInput.Model.Name.Replace('/', Path.DirectorySeparatorChar)
                };
            }, "4");
        }, -10);
        AddStep(g =>
        {
            g.CreateNode("EmptyLatentImage", (_, n) =>
            {
                n["inputs"] = new JObject()
                {
                    ["batch_size"] = "1",
                    ["height"] = g.UserInput.Height,
                    ["width"] = g.UserInput.Width
                };
            }, "5");
        }, -9);
        AddStep(g =>
        {
            g.CreateNode("CLIPTextEncode", (_, n) =>
            {
                n["inputs"] = new JObject()
                {
                    ["clip"] = g.FinalClip,
                    ["text"] = g.UserInput.Prompt
                };
            }, "6");
        }, -8);
        AddStep(g =>
        {
            g.CreateNode("CLIPTextEncode", (_, n) =>
            {
                n["inputs"] = new JObject()
                {
                    ["clip"] = g.FinalClip,
                    ["text"] = g.UserInput.NegativePrompt
                };
            }, "7");
        }, -7);
        AddStep(g =>
        {
            g.CreateNode("KSamplerAdvanced", (_, n) =>
            {
                n["inputs"] = new JObject()
                {
                    ["model"] = g.FinalModel,
                    ["add_noise"] = "enable",
                    ["noise_seed"] = g.UserInput.Seed,
                    ["steps"] = g.UserInput.Steps,
                    ["cfg"] = g.UserInput.CFGScale,
                    // TODO: proper sampler input, and intelligent default scheduler per sampler
                    ["sampler_name"] = g.UserInput.OtherParams.GetValueOrDefault("comfy_sampler", "euler").ToString(),
                    ["scheduler"] = g.UserInput.OtherParams.GetValueOrDefault("comfy_scheduler", "normal").ToString(),
                    ["positive"] = g.FinalPrompt,
                    ["negative"] = g.FinalNegativePrompt,
                    ["latent_image"] = g.FinalLatentImage,
                    // TODO: Configurable
                    ["start_at_step"] = 0,
                    ["end_at_step"] = 10000,
                    ["return_with_leftover_noise"] = "disable"
                };
            }, "10");
        }, -1);
        AddStep(g =>
        {
            g.CreateNode("VAEDecode", (_, n) =>
            {
                n["inputs"] = new JObject()
                {
                    ["samples"] = g.FinalSamples,
                    ["vae"] = g.FinalVae
                };
            }, "8");
        }, 9);
        AddStep(g =>
        {
            g.CreateNode("SaveImage", (_, n) =>
            {
                n["inputs"] = new JObject()
                {
                    ["filename_prefix"] = $"StableUI_{Random.Shared.Next():X4}_",
                    ["images"] = g.FinalImageOut
                };
            }, "9");
        }, 10);
    }

    /// <summary>The raw user input data.</summary>
    public T2IParams UserInput;

    /// <summary>The output workflow object.</summary>
    public JObject Workflow;

    /// <summary>Lastmost node ID for key input trackers.</summary>
    public JArray FinalModel = new() { "4", 0 },
        FinalClip = new() { "4", 1 },
        FinalVae = new() { "4", 2 },
        FinalLatentImage = new() { "5", 0 },
        FinalPrompt = new() { "6", 0 },
        FinalNegativePrompt = new() { "7", 0 },
        FinalSamples = new() { "10", 0 },
        FinalImageOut = new() { "8", 0 };

    /// <summary>Mapping of any extra nodes to keep track of, Name->ID, eg "MyNode" -> "15".</summary>
    public Dictionary<string, string> NodeHelpers = new();

    /// <summary>Last used ID, tracked to safely add new nodes with sequential IDs. Note that this starts at 100, as below 100 is reserved for constant node IDs.</summary>
    public int LastID = 100;

    /// <summary>Creates a new node with the given class type and configuration action.</summary>
    public void CreateNode(string classType, Action<string, JObject> configure)
    {
        int id = LastID++;
        CreateNode(classType, configure, $"{id}");
    }

    /// <summary>Creates a new node with the given class type and configuration action, and manual ID.</summary>
    public void CreateNode(string classType, Action<string, JObject> configure, string id)
    {
        JObject obj = new() { ["class_type"] = classType };
        configure(id, obj);
        Workflow[id] = obj;
    }

    /// <summary>Call to run the generation process and get the result.</summary>
    public JObject Generate()
    {
        Workflow = new();
        foreach (WorkflowGenStep step in Steps)
        {
            step.Action(this);
        }
        return Workflow;
    }
}
