using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;
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
        if (g?.UserInput is null || !g.UserInput.TryGet(Base2EditExtension.EditModel, out _))
        {
            return;
        }

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
        string prompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return applyAfter == expectedApplyAfter;
    }

    private static void RunEditStage(WorkflowGenerator g, bool isFinalStep)
    {
        var prompts = new EditPrompts(
            ExtractPrompt(g.UserInput.Get(T2IParamTypes.Prompt, "")),
            ExtractPrompt(g.UserInput.Get(T2IParamTypes.NegativePrompt, ""))
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
    }

    private static EditModelState PrepareEditModelAndVae(WorkflowGenerator g)
    {
        JArray preEditVae = g.FinalVae;
        JArray model = g.FinalModel;
        JArray clip = g.FinalClip;
        JArray vae = g.FinalVae;

        if (!ModelPrep.TryResolveEditModel(g, Base2EditExtension.EditModel, out T2IModel editModel, out var mustReencode))
        {
            return new EditModelState(model, clip, vae, preEditVae, mustReencode);
        }

        // If the <edit> section defines lora, load the model in an isolated context and apply those lora
        (List<string> Loras, List<string> Weights, List<string> TencWeights) loras = ExtractEditPromptLoras(g);

        if (loras.Loras.Count > 0)
        {
            (model, clip, vae) = ModelPrep.LoadEditModelWithIsolatedLoras(
                g,
                editModel,
                sectionId: Base2EditExtension.SectionID_Edit,
                getEditPromptLoras: _ => (loras.Loras, loras.Weights, loras.TencWeights)
            );
        }
        else
        {
            (model, clip, vae) = ModelPrep.LoadEditModelWithoutLoras(
                g,
                editModel,
                sectionId: Base2EditExtension.SectionID_Edit
            );
        }

        if (g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel altEditVae)
            && altEditVae is not null
            && altEditVae.Name != "Automatic")
        {
            mustReencode = true;
            vae = g.CreateVAELoader(altEditVae.ToString(g.ModelFolderFormat));
            g.FinalVae = vae;
        }

        return new EditModelState(model, clip, vae, preEditVae, mustReencode);
    }

    private static (List<string> Loras, List<string> Weights, List<string> TencWeights) ExtractEditPromptLoras(WorkflowGenerator g)
    {
        if (g?.UserInput is null
            || !g.UserInput.TryGet(T2IParamTypes.Loras, out List<string> loras)
            || loras is null
            || loras.Count == 0)
        {
            return ([], [], []);
        }

        List<string> weights = g.UserInput.Get(T2IParamTypes.LoraWeights) ?? [];
        List<string> tencWeights = g.UserInput.Get(T2IParamTypes.LoraTencWeights) ?? [];
        List<string> confinements = g.UserInput.Get(T2IParamTypes.LoraSectionConfinement) ?? [];

        if (confinements.Count == 0)
        {
            return ([], [], []);
        }

        List<string> outLoras = [];
        List<string> outWeights = [];
        List<string> outTencWeights = [];

        for (int i = 0; i < loras.Count; i++)
        {
            if (i >= confinements.Count)
            {
                continue;
            }

            if (!int.TryParse(confinements[i], out int confinementId))
            {
                continue;
            }

            if (confinementId != Base2EditExtension.SectionID_Edit)
            {
                continue;
            }

            outLoras.Add(loras[i]);
            outWeights.Add(i < weights.Count ? weights[i] : "1");
            outTencWeights.Add(i < tencWeights.Count ? tencWeights[i] : (i < weights.Count ? weights[i] : "1"));
        }

        return (outLoras, outWeights, outTencWeights);
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
        if (g.FinalImageOut is not null)
        {
            return;
        }

        // If a prior stage already created a decode node for the current samples,
        // reuse it instead of emitting a duplicate VAEDecode.
        if (VaeNodeReuse.ReuseVaeDecodeForSamples(g, g.FinalSamples, out JArray reusedImage))
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
        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, g.FinalImageOut, modelState.Vae, out JArray reusedSamples))
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
    )
    {
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
    )
    {
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
    )
    {
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
        if (!isFinalStep)
        {
            g.FinalImageOut = null;
            return;
        }

        // If a decode node already exists for the current samples with the intended VAE,
        // reuse it instead of emitting a duplicate VAEDecode.
        if (VaeNodeReuse.ReuseVaeDecodeForSamplesAndVae(g, g.FinalSamples, vae, out JArray reusedImage))
        {
            g.FinalImageOut = reusedImage;
            return;
        }

        g.FinalImageOut = [g.CreateVAEDecode(vae, g.FinalSamples), 0];
    }

    private static string ExtractPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("<edit"))
        {
            return "";
        }

        HashSet<string> sectionEndingTags = ["base", "refiner", "video", "videoswap", "region", "segment", "object"];
        string result = "";
        string[] pieces = prompt.Split('<');
        bool inEditSection = false;

        foreach (string piece in pieces)
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                if (inEditSection)
                {
                    result += "<" + piece;
                }
                continue;
            }

            string tag = piece[..end];
            // Handle <edit>, <edit:data>, and <edit//cid=X> formats
            string tagPrefix = tag.Split(':')[0].Split('/')[0];
            string content = piece[(end + 1)..];

            if (tagPrefix == "edit")
            {
                inEditSection = true;
                result += content;
            }
            else if (inEditSection)
            {
                if (sectionEndingTags.Contains(tagPrefix))
                {
                    break;
                }
                else
                {
                    result += "<" + piece;
                }
            }
        }

        return result.Trim();
    }
}
