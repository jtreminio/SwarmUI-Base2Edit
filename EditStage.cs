using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Accounts;
using Newtonsoft.Json.Linq;

namespace Base2Edit;

internal record EditPrompts(string Positive, string Negative);

internal record EditModelState(
    JArray Model,
    JArray Clip,
    JArray Vae,
    JArray PreEditVae,
    bool MustReencode
);

internal record EditParameters(
    int Width,
    int Height,
    int Steps,
    double Cfg,
    double Control,
    double Guidance,
    long Seed,
    string Sampler,
    string Scheduler
);

internal record EditConditioning(JArray Positive, JArray Negative);

public class EditStage
{
    private const int PreEditImageSaveId = 50200;
    private const int EditSeedOffset = 2;

    public static void Run(WorkflowGenerator g, bool isFinalStep)
    {
        if (!isFinalStep)
        {
            CaptureBaseStageModelState(g);
        }

        if (!isFinalStep && ShouldRunEditStage(g, "Base"))
        {
            RunEditStage(g, isFinalStep: false);
        }
        else if (isFinalStep && ShouldRunEditStage(g, "Refiner"))
        {
            RunEditStage(g, isFinalStep: true);
        }
    }

    private static bool ShouldRunEditStage(WorkflowGenerator g, string expectedApplyAfter)
    {
        string applyAfter = g.UserInput.Get(Base2EditExtension.ApplyEditAfter, "Refiner");

        if (!EditPromptParser.HasEditSection(g.UserInput.Get(T2IParamTypes.Prompt, "")))
        {
            return false;
        }

        return applyAfter == expectedApplyAfter;
    }

    private static void RunEditStage(WorkflowGenerator g, bool isFinalStep)
    {
        var prompts = new EditPrompts(
            EditPromptParser.Extract(g.UserInput.Get(T2IParamTypes.Prompt, "")),
            EditPromptParser.Extract(g.UserInput.Get(T2IParamTypes.NegativePrompt, ""))
        );
        var modelState = PrepareEditModelAndVae(g);

        bool shouldSavePreEdit = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(Base2EditExtension.KeepPreEditImage, false);
        bool needsPreEditImage = shouldSavePreEdit || modelState.MustReencode || g.FinalSamples is null;

        if (needsPreEditImage)
        {
            EnsureImageAvailable(g, modelState.PreEditVae);
        }

        if (shouldSavePreEdit)
        {
            SavePreEditImageIfNeeded(g);
        }

        ReencodeIfNeeded(g, modelState);
        var editParams = new EditParameters(
            Width: g.UserInput.GetImageWidth(),
            Height: g.UserInput.GetImageHeight(),
            Steps: g.UserInput.Get(Base2EditExtension.EditSteps, 20),
            Cfg: g.UserInput.Get(Base2EditExtension.EditCFGScale, 7.0),
            Control: g.UserInput.Get(Base2EditExtension.EditControl, 1.0),
            Guidance: g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1),
            Seed: g.UserInput.Get(T2IParamTypes.Seed) + EditSeedOffset,
            Sampler: g.UserInput.Get(Base2EditExtension.EditSampler, "euler"),
            Scheduler: g.UserInput.Get(Base2EditExtension.EditScheduler, "normal")
        );
        var conditioning = CreateEditConditioning(g, modelState.Clip, prompts, editParams);
        ExecuteEditSampler(g, modelState.Model, conditioning, editParams);

        // If the edit stage is injected mid-workflow (after Base but before upscale/refiner),
        // subsequent steps will decode/encode the current latent using g.FinalVae.
        // Ensure it matches the VAE that corresponds to the edited latent.
        if (!isFinalStep)
        {
            g.FinalVae = modelState.Vae;
        }

        FinalizeEditOutput(g, modelState.Vae, isFinalStep);
        var applyAfter = g.UserInput.Get(Base2EditExtension.ApplyEditAfter, "Refiner");

        int loraNodes;
        try
        {
            loraNodes = WorkflowUtils.NodesOfType(g.Workflow, "LoraLoader").Count;
        }
        catch (Exception ex)
        {
            Logs.Error($"Base2Edit: Failed to inspect workflow graph (LoraLoader count): {ex}");
            return;
        }

        Logs.Debug($"Base2Edit: Applied edit stage after {applyAfter} with {loraNodes} LoraLoader nodes");
    }

    private static EditModelState PrepareEditModelAndVae(WorkflowGenerator g)
    {
        JArray preEditVae = g.FinalVae;
        JArray model = g.FinalModel;
        JArray clip = g.FinalClip;
        JArray vae = g.FinalVae;

        // Determine which model the edit stage should use.
        if (!EditStageModelPreparation.TryResolveEditModel(g, Base2EditExtension.EditModel, out T2IModel editModel, out var mustReencode))
        {
            return new EditModelState(model, clip, vae, preEditVae, mustReencode);
        }

        // If the <edit> section defines LoRAs, load the model in an isolated context and apply those LoRAs
        // via dedicated model+LoRA nodes. Otherwise, load the model directly (no LoRA node).
        (List<string> Loras, List<string> Weights, List<string> TencWeights) loras = ([], [], []);
        bool hasEditPromptLoras = false;
        if (EditPromptMentionsLora(g))
        {
            try
            {
                loras = ExtractEditPromptLoras(g);
                hasEditPromptLoras = loras.Loras.Count > 0;
            }
            catch (Exception ex)
            {
                Logs.Error($"Base2Edit: Failed to parse <edit> LoRAs, continuing without edit-stage LoRAs: {ex}");
                hasEditPromptLoras = false;
            }
        }

        if (hasEditPromptLoras)
        {
            (model, clip, vae) = EditStageModelPreparation.LoadEditModelWithIsolatedLoras(
                g,
                editModel,
                sectionId: Base2EditExtension.SectionID_Edit,
                getEditPromptLoras: _ => (loras.Loras, loras.Weights, loras.TencWeights)
            );
        }
        else
        {
            (model, clip, vae) = EditStageModelPreparation.LoadEditModelWithoutLoras(
                g,
                editModel,
                sectionId: Base2EditExtension.SectionID_Edit
            );
        }

        if (g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel altEditVae) && altEditVae is not null && altEditVae.Name != "Automatic")
        {
            mustReencode = true;
            vae = g.CreateVAELoader(altEditVae.ToString(g.ModelFolderFormat));
            g.FinalVae = vae;
        }

        return new EditModelState(model, clip, vae, preEditVae, mustReencode);
    }

    private static (List<string> Loras, List<string> Weights, List<string> TencWeights) ExtractEditPromptLoras(WorkflowGenerator g)
    {
        string getOriginalOrCurrent(T2IRegisteredParam<string> param)
        {
            string key = $"original_{param.Type.ID}";
            if (g.UserInput.ExtraMeta.TryGetValue(key, out object obj) && obj is not null)
            {
                return obj.ToString();
            }

            return g.UserInput.Get(param, "");
        }

        string editPos = EditPromptParser.Extract(getOriginalOrCurrent(T2IParamTypes.Prompt));
        string editNeg = EditPromptParser.Extract(getOriginalOrCurrent(T2IParamTypes.NegativePrompt));
        string combined = $"{editPos} {editNeg}";
        string[] available = [.. Program.T2IModelSets["LoRA"].ListModelNamesFor(g.UserInput.SourceSession)];

        return LoraParsing.ParseEditPromptLoras(combined, available);
    }

    private static bool EditPromptMentionsLora(WorkflowGenerator g)
    {
        string getOriginalOrCurrent(T2IRegisteredParam<string> param)
        {
            string key = $"original_{param.Type.ID}";
            if (g.UserInput.ExtraMeta.TryGetValue(key, out object obj) && obj is not null)
            {
                return obj.ToString();
            }

            return g.UserInput.Get(param, "");
        }

        string editPos = EditPromptParser.Extract(getOriginalOrCurrent(T2IParamTypes.Prompt));
        string editNeg = EditPromptParser.Extract(getOriginalOrCurrent(T2IParamTypes.NegativePrompt));
        string combined = $"{editPos} {editNeg}";
        return combined.IndexOf("<lora:", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void CaptureBaseStageModelState(WorkflowGenerator g)
    {
        // Best-effort snapshot; used only as a fallback for later steps.
        const string kModel = "base2edit.base_model_ref";
        const string kClip = "base2edit.base_clip_ref";
        const string kVae = "base2edit.base_vae_ref";

        if (g?.UserInput?.ExtraMeta is null)
        {
            return;
        }

        if (!g.UserInput.ExtraMeta.ContainsKey(kModel) && g.FinalModel is JArray fm && fm.Count == 2)
        {
            g.UserInput.ExtraMeta[kModel] = new JArray(fm[0], fm[1]);
        }
        if (!g.UserInput.ExtraMeta.ContainsKey(kClip) && g.FinalClip is JArray fc && fc.Count == 2)
        {
            g.UserInput.ExtraMeta[kClip] = new JArray(fc[0], fc[1]);
        }
        if (!g.UserInput.ExtraMeta.ContainsKey(kVae) && g.FinalVae is JArray fv && fv.Count == 2)
        {
            g.UserInput.ExtraMeta[kVae] = new JArray(fv[0], fv[1]);
        }
    }

    private static void EnsureImageAvailable(WorkflowGenerator g, JArray preEditVae)
    {
        if (g.FinalImageOut is not null) {
            return;
        }

        // If a prior stage already created a decode node for the current samples,
        // reuse it instead of emitting a duplicate VAEDecode.
        if (WorkflowNodeReuse.ReuseVaeDecodeForSamples(g, g.FinalSamples, out JArray reusedImage))
        {
            g.FinalImageOut = reusedImage;
            return;
        }

        string decodeNode = g.CreateVAEDecode(preEditVae, g.FinalSamples);
        g.FinalImageOut = [decodeNode, 0];
    }

    private static void SavePreEditImageIfNeeded(WorkflowGenerator g)
    {
        bool shouldSave = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(Base2EditExtension.KeepPreEditImage, false);

        if (shouldSave)
        {
            g.CreateImageSaveNode(g.FinalImageOut, g.GetStableDynamicID(PreEditImageSaveId, 0));
            Logs.Debug("Base2Edit: Saved pre-edit image");
        }
    }

    private static void ReencodeIfNeeded(WorkflowGenerator g, EditModelState modelState)
    {
        if (!modelState.MustReencode && g.FinalSamples is not null)
        {
            return;
        }

        // If a prior stage already encoded the current image to latents with the intended VAE,
        // reuse it instead of emitting a duplicate VAEEncode.
        if (WorkflowNodeReuse.ReuseVaeEncodeForImage(g, g.FinalImageOut, modelState.Vae, out JArray reusedSamples))
        {
            g.FinalSamples = reusedSamples;
            return;
        }

        string encodeNode = g.CreateVAEEncode(modelState.Vae, g.FinalImageOut);
        g.FinalSamples = [encodeNode, 0];
    }

    private static EditConditioning CreateEditConditioning(
        WorkflowGenerator g,
        JArray clip,
        EditPrompts prompts,
        EditParameters editParams
    ) {
        string posCondNode = CreateConditioningNode(g, clip, prompts.Positive, editParams);
        string refLatentNode = g.CreateNode("ReferenceLatent", new JObject()
        {
            ["conditioning"] = new JArray { posCondNode, 0 },
            ["latent"] = g.FinalSamples
        });
        string negCondNode = CreateConditioningNode(g, clip, prompts.Negative, editParams);

        return new EditConditioning([refLatentNode, 0], [negCondNode, 0]);
    }

    private static string CreateConditioningNode(
        WorkflowGenerator g,
        JArray clip,
        string prompt,
        EditParameters editParams
    ) {
        return g.CreateNode("SwarmClipTextEncodeAdvanced", new JObject()
        {
            ["clip"] = clip,
            ["steps"] = editParams.Steps,
            ["prompt"] = prompt,
            ["width"] = editParams.Width,
            ["height"] = editParams.Height,
            ["target_width"] = editParams.Width,
            ["target_height"] = editParams.Height,
            ["guidance"] = editParams.Guidance
        });
    }

    private static void ExecuteEditSampler(
        WorkflowGenerator g,
        JArray model,
        EditConditioning conditioning,
        EditParameters editParams
    ) {
        int startStep = (int)Math.Round(editParams.Steps * (1 - editParams.Control));
        string samplerNode = g.CreateKSampler(
            model,
            conditioning.Positive,
            conditioning.Negative,
            g.FinalSamples,
            editParams.Cfg,
            editParams.Steps,
            startStep,
            10000,
            editParams.Seed,
            returnWithLeftoverNoise: false,
            addNoise: true,
            explicitSampler: editParams.Sampler,
            explicitScheduler: editParams.Scheduler,
            sectionId: Base2EditExtension.SectionID_Edit
        );

        g.FinalSamples = [samplerNode, 0];
    }

    private static void FinalizeEditOutput(WorkflowGenerator g, JArray vae, bool isFinalStep)
    {
        if (!isFinalStep) {
            g.FinalImageOut = null;
            return;
        }

        // If a decode node already exists for the current samples with the intended VAE,
        // reuse it instead of emitting a duplicate VAEDecode.
        if (WorkflowNodeReuse.ReuseVaeDecodeForSamplesAndVae(g, g.FinalSamples, vae, out JArray reusedImage))
        {
            g.FinalImageOut = reusedImage;
            return;
        }

        g.FinalImageOut = [g.CreateVAEDecode(vae, g.FinalSamples), 0];
    }
}