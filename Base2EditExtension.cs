using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;
using Newtonsoft.Json.Linq;

namespace Base2Edit;

public class Base2EditExtension : Extension
{
    public static T2IParamGroup Base2EditGroup;
    public static T2IRegisteredParam<bool> KeepBaseImage;

    public override void OnPreInit()
    {
    }

    public override void OnInit()
    {
        Logs.Info("Base2Edit Extension initializing...");
        RegisterParameters();
        RegisterWorkflowSteps();
    }

    private void RegisterParameters()
    {
        Base2EditGroup = new(
            Name: "Base2Edit",
            Description: "Lets you edit the base image directly using the refiner model.\nFor best results, set Refiner Control to 1.",
            Toggles: true,
            Open: false,
            OrderPriority: 8
        );

        KeepBaseImage = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Keep Base Image",
            Description: "When enabled, saves the base image (pre-refine/edit stage) alongside the final output.",
            Default: "false",
            Group: Base2EditGroup,
            OrderPriority: 1,
            FeatureFlag: "comfyui"
        ));
    }

    private static bool IsBase2EditEnabled(WorkflowGenerator g)
    {
        return g.UserInput.TryGet(KeepBaseImage, out _);
    }

    private static bool IsRefinerConfigured(WorkflowGenerator g)
    {
        return g.UserInput.TryGet(T2IParamTypes.RefinerMethod, out _)
            && g.UserInput.TryGet(T2IParamTypes.RefinerControl, out _);
    }

    private void RegisterWorkflowSteps()
    {
        WorkflowGenerator.AddStep(SaveBaseImageStep, -4.1);
        WorkflowGenerator.AddStep(ReplaceRefinerConditioningStep, -3.9);
    }

    private static void SaveBaseImageStep(WorkflowGenerator g)
    {
        if (!IsBase2EditEnabled(g) || !IsRefinerConfigured(g) || !g.UserInput.Get(KeepBaseImage, false))
        {
            return;
        }

        string baseVaeDecode = g.CreateNode("VAEDecode", new JObject()
        {
            ["samples"] = g.FinalSamples,
            ["vae"] = g.FinalVae
        });

        g.CreateImageSaveNode([baseVaeDecode, 0], g.GetStableDynamicID(50100, 0));
        Logs.Debug("Base2Edit: Created base image save node before refiner");
    }

    private static void ReplaceRefinerConditioningStep(WorkflowGenerator g)
    {
        if (!IsBase2EditEnabled(g) || !IsRefinerConfigured(g))
        {
            return;
        }

        if (!g.HasNode("23"))
        {
            Logs.Warning("Base2Edit: Refiner sampler node not found");
            return;
        }

        string latentNodeId = g.HasNode("25") ? "25" : (g.HasNode("26") ? "26" : null);

        if (g.Workflow["23"] is not JObject samplerNode)
        {
            Logs.Warning("Base2Edit: Could not access refiner sampler node");
            return;
        }

        if (samplerNode["inputs"] is not JObject samplerInputs)
        {
            Logs.Warning("Base2Edit: Could not access refiner sampler inputs");
            return;
        }

        string rawPrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string rawNegPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");

        PromptRegion posRegion = new(rawPrompt);
        PromptRegion negRegion = new(rawNegPrompt);

        string positivePrompt = posRegion.GlobalPrompt;
        if (!string.IsNullOrWhiteSpace(posRegion.RefinerPrompt))
        {
            positivePrompt = $"{positivePrompt} {posRegion.RefinerPrompt}".Trim();
        }

        string negativePrompt = negRegion.GlobalPrompt;
        if (!string.IsNullOrWhiteSpace(negRegion.RefinerPrompt))
        {
            negativePrompt = $"{negativePrompt} {negRegion.RefinerPrompt}".Trim();
        }

        int width = g.UserInput.GetImageWidth();
        int height = g.UserInput.GetImageHeight();
        int steps = g.UserInput.Get(
            T2IParamTypes.RefinerSteps,
            g.UserInput.Get(T2IParamTypes.Steps, 20, sectionId: T2IParamInput.SectionID_Refiner),
            sectionId: T2IParamInput.SectionID_Refiner
        );
        double guidance = g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1);

        string posCondNode = g.CreateNode("SwarmClipTextEncodeAdvanced", new JObject()
        {
            ["clip"] = g.FinalClip,
            ["steps"] = steps,
            ["prompt"] = positivePrompt,
            ["width"] = width,
            ["height"] = height,
            ["target_width"] = width,
            ["target_height"] = height,
            ["guidance"] = guidance
        });

        JArray latentInput = latentNodeId != null
            ? new JArray { latentNodeId, 0 }
            : (samplerInputs["latent_image"] as JArray ?? g.FinalSamples);

        string refLatentNode = g.CreateNode("ReferenceLatent", new JObject()
        {
            ["conditioning"] = new JArray { posCondNode, 0 },
            ["latent"] = latentInput
        });

        string negCondNode = g.CreateNode("SwarmClipTextEncodeAdvanced", new JObject()
        {
            ["clip"] = g.FinalClip,
            ["steps"] = steps,
            ["prompt"] = negativePrompt,
            ["width"] = width,
            ["height"] = height,
            ["target_width"] = width,
            ["target_height"] = height,
            ["guidance"] = guidance
        });

        samplerInputs["positive"] = new JArray { refLatentNode, 0 };
        samplerInputs["negative"] = new JArray { negCondNode, 0 };

        Logs.Debug("Base2Edit: Replaced refiner conditioning with SwarmClipTextEncodeAdvanced + ReferenceLatent");
    }
}
