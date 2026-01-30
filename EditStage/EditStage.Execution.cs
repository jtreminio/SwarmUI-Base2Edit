using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public partial class EditStage
{
    private static void RunEditStage(WorkflowGenerator g, bool isFinalStep, int stageIndex)
    {
        var prompts = new EditPrompts(
            ExtractPrompt(g.UserInput.Get(T2IParamTypes.Prompt, ""), stageIndex),
            ExtractPrompt(g.UserInput.Get(T2IParamTypes.NegativePrompt, ""), stageIndex)
        );
        var modelState = PrepareEditModelAndVae(g, stageIndex);

        bool shouldSavePreEdit = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(Base2EditExtension.KeepPreEditImage, false);
        bool needsPreEditImage = shouldSavePreEdit || modelState.MustReencode || g.FinalSamples is null;

        if (needsPreEditImage)
        {
            EnsureImageAvailable(g, modelState.PreEditVae);
        }

        if (shouldSavePreEdit)
        {
            SavePreEditImageIfNeeded(g, stageIndex);
        }

        ReencodeIfNeeded(g, modelState);
        var editParams = new EditParameters(
            Width: g.UserInput.GetImageWidth(),
            Height: g.UserInput.GetImageHeight(),
            Steps: g.UserInput.Get(Base2EditExtension.EditSteps, 20),
            Cfg: g.UserInput.Get(Base2EditExtension.EditCFGScale, 7.0),
            Control: g.UserInput.Get(Base2EditExtension.EditControl, 1.0),
            Guidance: g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1),
            Seed: g.UserInput.Get(T2IParamTypes.Seed) + EditSeedOffset + stageIndex,
            Sampler: g.UserInput.Get(Base2EditExtension.EditSampler, "euler"),
            Scheduler: g.UserInput.Get(Base2EditExtension.EditScheduler, "normal")
        );
        var conditioning = CreateEditConditioning(g, modelState.Clip, prompts, editParams);
        ExecuteEditSampler(g, modelState.Model, conditioning, editParams, stageIndex);

        // Keep g.FinalVae aligned with the current samples for any subsequent stages
        g.FinalVae = modelState.Vae;

        // If the edit stage is injected mid-workflow (after Base but before upscale/refiner),
        // subsequent steps will decode/encode the current latent using g.FinalVae.
        // Ensure it matches the VAE that corresponds to the edited latent.
        if (!isFinalStep)
        {
            g.FinalVae = modelState.Vae;
        }

        FinalizeEditOutput(g, modelState.Vae, isFinalStep);
    }

    private static EditModelState PrepareEditModelAndVae(WorkflowGenerator g, int stageIndex)
    {
        int stageSectionId = Base2EditExtension.EditSectionIdForStage(stageIndex);
        JArray preEditVae = g.FinalVae;
        JArray model = g.FinalModel;
        JArray clip = g.FinalClip;
        JArray vae = g.FinalVae;

        if (!ModelPrep.TryResolveEditModel(g, Base2EditExtension.EditModel, out T2IModel editModel, out var mustReencode))
        {
            return new EditModelState(model, clip, vae, preEditVae, mustReencode);
        }

        // If the <edit> / <edit[n]> section defines lora, load the model in an isolated context and apply those lora
        (List<string> Loras, List<string> Weights, List<string> TencWeights) loras = ExtractEditPromptLoras(g, stageIndex);

        if (loras.Loras.Count > 0)
        {
            (model, clip, vae) = ModelPrep.LoadEditModelWithIsolatedLoras(
                g,
                editModel,
                sectionId: stageSectionId,
                getEditPromptLoras: _ => (loras.Loras, loras.Weights, loras.TencWeights)
            );
        }
        else
        {
            (model, clip, vae) = ModelPrep.LoadEditModelWithoutLoras(g, editModel, sectionId: stageSectionId);
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

    private static (List<string> Loras, List<string> Weights, List<string> TencWeights) ExtractEditPromptLoras(WorkflowGenerator g, int stageIndex)
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

        int globalCid = Base2EditExtension.SectionID_Edit;
        int stageCid = Base2EditExtension.EditSectionIdForStage(stageIndex);

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

            // Global (<edit>) always applies to every stage.
            // Stage-specific (<edit[n]>) applies only to the targeted stage.
            if (confinementId != globalCid && confinementId != stageCid)
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
        // Best-effort snapshot; used only as a fallback for later steps
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

    private static void SavePreEditImageIfNeeded(WorkflowGenerator g, int stageIndex)
    {
        bool shouldSave = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(Base2EditExtension.KeepPreEditImage, false);

        if (shouldSave)
        {
            g.CreateImageSaveNode(g.FinalImageOut, g.GetStableDynamicID(PreEditImageSaveId, stageIndex));
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
        EditParameters editParams,
        int stageIndex
    )
    {
        int startStep = (int)Math.Round(editParams.Steps * (1 - editParams.Control));
        int stageSectionId = Base2EditExtension.EditSectionIdForStage(stageIndex);
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
            sectionId: stageSectionId
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

    private static string ExtractPrompt(string prompt, int stageIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        HashSet<string> sectionEndingTags = ["base", "refiner", "video", "videoswap", "region", "segment", "object"];
        int globalCid = Base2EditExtension.SectionID_Edit;
        int stageCid = Base2EditExtension.EditSectionIdForStage(stageIndex);

        static void AppendWithBoundarySpace(ref string dest, string add)
        {
            if (string.IsNullOrEmpty(add))
            {
                return;
            }
            if (!string.IsNullOrEmpty(dest)
                && !char.IsWhiteSpace(dest[^1])
                && !char.IsWhiteSpace(add[0]))
            {
                dest += " ";
            }
            dest += add;
        }

        string result = "";
        string[] pieces = prompt.Split('<');
        bool inWantedSection = false;

        foreach (string piece in pieces)
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                if (inWantedSection)
                {
                    result += "<" + piece;
                }
                continue;
            }

            string tag = piece[..end];
            string content = piece[(end + 1)..];

            // Determine tag prefix (for section ending tags, and for raw <edit[...]>)
            string prefixPart = tag;
            int colon = tag.IndexOf(':');
            if (colon != -1)
            {
                prefixPart = tag[..colon];
            }
            prefixPart = prefixPart.Split('/')[0];
            string prefixName = prefixPart;
            string preData = null;
            if (prefixName.EndsWith(']') && prefixName.Contains('['))
            {
                int open = prefixName.LastIndexOf('[');
                if (open != -1)
                {
                    preData = prefixName[(open + 1)..^1];
                    prefixName = prefixName[..open];
                }
            }
            string tagPrefixLower = prefixName.ToLowerInvariant();

            // Handle:
            // - <edit> / <edit:data>
            // - <edit[n]> / <edit[n]:data>
            // - <edit//cid=X> (post-processed prompt syntax)
            bool isEditTag = tagPrefixLower == "edit";
            if (isEditTag)
            {
                bool wantThisSection = false;

                // Prefer cid when present (post-processed form keeps intent even after [n] is removed)
                int cidCut = tag.LastIndexOf("//cid=", StringComparison.OrdinalIgnoreCase);
                if (cidCut != -1 && int.TryParse(tag[(cidCut + "//cid=".Length)..], out int cid))
                {
                    wantThisSection = cid == globalCid || cid == stageCid;
                }
                else if (preData is null)
                {
                    // Global: <edit> (applies to all stages)
                    wantThisSection = true;
                }
                else if (int.TryParse(preData, out int tagStage) && tagStage == stageIndex)
                {
                    // Stage-specific: <edit[n]>
                    wantThisSection = true;
                }

                inWantedSection = wantThisSection;
                if (inWantedSection)
                {
                    AppendWithBoundarySpace(ref result, content);
                }
            }
            else if (inWantedSection)
            {
                if (sectionEndingTags.Contains(tagPrefixLower))
                {
                    inWantedSection = false;
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
