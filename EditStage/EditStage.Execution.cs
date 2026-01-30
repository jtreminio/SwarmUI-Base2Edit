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
        var modelState = PrepareEditModelAndVae(g, isFinalStep, stageIndex);
        string modelSelection = g.UserInput.Get(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
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
            Cfg: ResolveInheritedCfg(g, modelSelection),
            Control: g.UserInput.Get(Base2EditExtension.EditControl, 1.0),
            Guidance: g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1),
            Seed: g.UserInput.Get(T2IParamTypes.Seed) + EditSeedOffset + stageIndex,
            Sampler: ResolveInheritedSampler(g, modelSelection),
            Scheduler: ResolveInheritedScheduler(g, modelSelection)
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

    private static double ResolveInheritedCfg(WorkflowGenerator g, string modelSelection)
    {
        if (g.UserInput.TryGet(Base2EditExtension.EditCFGScale, out double editCfg))
        {
            return editCfg;
        }

        // Inherit from whichever stage "(Use Base)" / "(Use Refiner)" points to
        if (string.Equals(modelSelection, ModelPrep.UseBase, StringComparison.OrdinalIgnoreCase))
        {
            return g.UserInput.Get(T2IParamTypes.CFGScale, 7);
        }

        if (string.Equals(modelSelection, ModelPrep.UseRefiner, StringComparison.OrdinalIgnoreCase))
        {
            return g.UserInput.Get(
                T2IParamTypes.RefinerCFGScale,
                g.UserInput.Get(T2IParamTypes.CFGScale, 7, sectionId: T2IParamInput.SectionID_Refiner),
                sectionId: T2IParamInput.SectionID_Refiner
            );
        }

        return 7;
    }

    private static string ResolveInheritedSampler(WorkflowGenerator g, string modelSelection)
    {
        if (g.UserInput.TryGet(Base2EditExtension.EditSampler, out string editSampler) && !string.IsNullOrWhiteSpace(editSampler))
        {
            return editSampler;
        }

        if (string.Equals(modelSelection, ModelPrep.UseRefiner, StringComparison.OrdinalIgnoreCase))
        {
            return g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
                ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSamplerParam, null)
                ?? g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler");
        }

        return g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler");
    }

    private static string ResolveInheritedScheduler(WorkflowGenerator g, string modelSelection)
    {
        if (g.UserInput.TryGet(Base2EditExtension.EditScheduler, out string editScheduler) && !string.IsNullOrWhiteSpace(editScheduler))
        {
            return editScheduler;
        }

        if (string.Equals(modelSelection, ModelPrep.UseRefiner, StringComparison.OrdinalIgnoreCase))
        {
            return g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
                ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSchedulerParam, null)
                ?? g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "normal");
        }

        return g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "normal");
    }

    private static EditModelState PrepareEditModelAndVae(WorkflowGenerator g, bool isFinalStep, int stageIndex)
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

        // - Prompt selection: if there is no <edit> / <edit[n]> section, we fall back to the global prompt text
        // - Model/LoRA selection:
        //   - "(Use Base)" and "(Use Refiner)" should inherit the stage's model+LoRAs
        //   - If an <edit> section is present and includes <lora>, those should be *added on top* of the inherited stack
        //   - If an explicit model name is selected, we load that model, apply only global UI LoRAs, and then apply any <edit> section LoRAs
        string selection = g.UserInput.Get(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        string posPrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        bool hasStageEditSection =
            HasAnyEditSectionForStage(posPrompt, stageIndex)
            || HasAnyEditSectionForStage(negPrompt, stageIndex);

        // Step 1: establish the base model/clip/vae stack for this stage
        if (string.Equals(selection, ModelPrep.UseBase, StringComparison.OrdinalIgnoreCase))
        {
            // Prefer captured base-stage stack (works even during the refiner/final phase)
            if (TryGetCapturedBaseStageModelState(g, out JArray baseModel, out JArray baseClip, out JArray baseVae))
            {
                model = baseModel;
                clip = baseClip;
                vae = baseVae;
            }
        }
        else if (string.Equals(selection, ModelPrep.UseRefiner, StringComparison.OrdinalIgnoreCase))
        {
            if (!isFinalStep)
            {
                // We haven't entered the refiner phase yet, but the user asked to use the refiner model+LoRAs.
                // Load the refiner model and apply the same UI LoRA confinements as the refiner stage.
                (model, clip, vae) = ModelPrep.LoadEditModelWithoutLoras(g, editModel, sectionId: stageSectionId);
                (model, clip) = g.LoadLorasForConfinement(-1, model, clip);
                (model, clip) = g.LoadLorasForConfinement(0, model, clip); // UI "Global"
                (model, clip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Refiner, model, clip);
            }
            // else: during final phase, g.FinalModel/g.FinalClip already represent the refiner stage stack
        }
        else
        {
            // Explicit model selection: load the model, then apply only global UI LoRAs (not Base/Refiner/etc)
            (model, clip, vae) = ModelPrep.LoadEditModelWithoutLoras(g, editModel, sectionId: stageSectionId);
            (model, clip) = g.LoadLorasForConfinement(-1, model, clip);
            (model, clip) = g.LoadLorasForConfinement(0, model, clip); // UI "Global"
        }

        // If the user is inheriting the refiner model stack before the refiner phase begins,
        // also inherit any refiner VAE override (so edit defaults match the refiner stage defaults)
        if (!isFinalStep
            && string.Equals(selection, ModelPrep.UseRefiner, StringComparison.OrdinalIgnoreCase)
            && !g.UserInput.TryGet(Base2EditExtension.EditVAE, out _)
            && g.UserInput.TryGet(T2IParamTypes.RefinerVAE, out T2IModel refinerVaeOverride)
            && refinerVaeOverride is not null)
        {
            mustReencode = true;
            vae = g.CreateVAELoader(refinerVaeOverride.ToString(g.ModelFolderFormat));
            g.FinalVae = vae;
        }

        // Step 2: if an <edit> section exists and includes <lora>, stack those LoRAs on top of the chosen model stack
        if (hasStageEditSection)
        {
            (List<string> Loras, List<string> Weights, List<string> TencWeights) loras = ExtractEditPromptLoras(g, stageIndex);
            if (loras.Loras.Count > 0)
            {
                // Apply edit-section LoRAs in isolation so we don't accidentally pick up UI-confined LoRAs
                LoraParamSnapshot snapshot = new(g);
                try
                {
                    snapshot.Remove();
                    List<string> confinements = [.. Enumerable.Repeat($"{stageSectionId}", loras.Loras.Count)];
                    g.UserInput.Set(T2IParamTypes.Loras, loras.Loras);
                    g.UserInput.Set(T2IParamTypes.LoraWeights, loras.Weights);
                    g.UserInput.Set(T2IParamTypes.LoraTencWeights, loras.TencWeights);
                    g.UserInput.Set(T2IParamTypes.LoraSectionConfinement, confinements);
                    (model, clip) = g.LoadLorasForConfinement(stageSectionId, model, clip);
                }
                finally
                {
                    snapshot.RestoreOrRemove();
                }
            }
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

    private sealed class LoraParamSnapshot
    {
        private readonly WorkflowGenerator _g;
        private readonly bool _hadLoras;
        private readonly bool _hadWeights;
        private readonly bool _hadTencWeights;
        private readonly bool _hadConfinements;
        private readonly List<string> _loras;
        private readonly List<string> _weights;
        private readonly List<string> _tencWeights;
        private readonly List<string> _confinements;

        public LoraParamSnapshot(WorkflowGenerator g)
        {
            _g = g;
            _hadLoras = g.UserInput.TryGet(T2IParamTypes.Loras, out _loras);
            _hadWeights = g.UserInput.TryGet(T2IParamTypes.LoraWeights, out _weights);
            _hadTencWeights = g.UserInput.TryGet(T2IParamTypes.LoraTencWeights, out _tencWeights);
            _hadConfinements = g.UserInput.TryGet(T2IParamTypes.LoraSectionConfinement, out _confinements);
        }

        public void Remove()
        {
            if (_hadLoras) _g.UserInput.Remove(T2IParamTypes.Loras);
            if (_hadWeights) _g.UserInput.Remove(T2IParamTypes.LoraWeights);
            if (_hadTencWeights) _g.UserInput.Remove(T2IParamTypes.LoraTencWeights);
            if (_hadConfinements) _g.UserInput.Remove(T2IParamTypes.LoraSectionConfinement);
        }

        public void RestoreOrRemove()
        {
            if (_hadLoras) _g.UserInput.Set(T2IParamTypes.Loras, _loras); else _g.UserInput.Remove(T2IParamTypes.Loras);
            if (_hadWeights) _g.UserInput.Set(T2IParamTypes.LoraWeights, _weights); else _g.UserInput.Remove(T2IParamTypes.LoraWeights);
            if (_hadTencWeights) _g.UserInput.Set(T2IParamTypes.LoraTencWeights, _tencWeights); else _g.UserInput.Remove(T2IParamTypes.LoraTencWeights);
            if (_hadConfinements) _g.UserInput.Set(T2IParamTypes.LoraSectionConfinement, _confinements); else _g.UserInput.Remove(T2IParamTypes.LoraSectionConfinement);
        }
    }

    private static bool TryGetCapturedBaseStageModelState(WorkflowGenerator g, out JArray model, out JArray clip, out JArray vae)
    {
        model = null;
        clip = null;
        vae = null;

        if (g?.UserInput?.ExtraMeta is null)
        {
            return false;
        }

        const string kModel = "base2edit.base_model_ref";
        const string kClip = "base2edit.base_clip_ref";
        const string kVae = "base2edit.base_vae_ref";

        if (!g.UserInput.ExtraMeta.TryGetValue(kModel, out object mObj)
            || !g.UserInput.ExtraMeta.TryGetValue(kClip, out object cObj)
            || !g.UserInput.ExtraMeta.TryGetValue(kVae, out object vObj))
        {
            return false;
        }

        if (mObj is not JArray m || m.Count != 2
            || cObj is not JArray c || c.Count != 2
            || vObj is not JArray v || v.Count != 2)
        {
            return false;
        }

        model = m;
        clip = c;
        vae = v;
        return true;
    }

    private static bool HasAnyEditSectionForStage(string prompt, int stageIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int globalCid = Base2EditExtension.SectionID_Edit;
        int stageCid = Base2EditExtension.EditSectionIdForStage(stageIndex);

        foreach (string piece in prompt.Split('<'))
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                continue;
            }

            string tag = piece[..end];

            // Determine tag prefix (for raw <edit[...]>)
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

            if (!string.Equals(prefixName, "edit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Processed syntax: <edit//cid=X>
            int cidCut = tag.LastIndexOf("//cid=", StringComparison.OrdinalIgnoreCase);
            if (cidCut != -1 && int.TryParse(tag[(cidCut + "//cid=".Length)..], out int cid))
            {
                if (cid == globalCid || cid == stageCid)
                {
                    return true;
                }
                continue;
            }

            // Raw syntax: <edit> or <edit[n]>
            if (preData is null)
            {
                return true; // <edit> (global)
            }
            if (int.TryParse(preData, out int tagStage) && tagStage == stageIndex)
            {
                return true; // <edit[n]>
            }
        }

        return false;
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

        if (shouldSave && g.FinalImageOut is not null && !VaeNodeReuse.HasSaveForImage(g, g.FinalImageOut))
        {
            g.CreateImageSaveNode(g.FinalImageOut, g.GetStableDynamicID(PreEditImageSaveId, stageIndex));
            Logs.Debug("Base2Edit: Saved pre-edit image");
        }
    }

    private static void ReencodeIfNeeded(WorkflowGenerator g, EditModelState modelState)
    {
        // Parallel stages (same anchor) reuse the same VAEDecode, so g.FinalImageOut points at
        // that decode's image. g.FinalSamples is still the anchor latent (ex base sampler output),
        // which is wrong for ReferenceLatent - we need the latent that comes from encoding this
        // image with the edit VAE. Reuse an existing VAEEncode for this image+VAE if the primary
        // stage already created one; otherwise we would feed raw anchor latent into the edit sampler.
        if (g.FinalImageOut is not null &&
            VaeNodeReuse.ReuseVaeEncodeForImage(g, g.FinalImageOut, modelState.Vae, out JArray reusedSamples))
        {
            g.FinalSamples = reusedSamples;
            return;
        }

        if (!modelState.MustReencode && g.FinalSamples is not null)
        {
            return;
        }

        if (g.FinalImageOut is null)
        {
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
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "";
        }

        // Fast path: no edit sections at all -> use full prompt as-is
        if (!prompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
        {
            return prompt.Trim();
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

        static string RemoveAllEditSections(string fullPrompt, HashSet<string> sectionEndingTagsLocal)
        {
            if (string.IsNullOrWhiteSpace(fullPrompt) || !fullPrompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
            {
                return (fullPrompt ?? "").Trim();
            }

            string resultLocal = "";
            bool inAnyEditSection = false;
            string[] piecesLocal = fullPrompt.Split('<');

            foreach (string piece in piecesLocal)
            {
                if (string.IsNullOrEmpty(piece))
                {
                    continue;
                }

                int end = piece.IndexOf('>');
                if (end == -1)
                {
                    if (!inAnyEditSection)
                    {
                        resultLocal += "<" + piece;
                    }
                    continue;
                }

                string tag = piece[..end];
                string content = piece[(end + 1)..];

                // Determine tag prefix
                string prefixPart = tag;
                int colon = tag.IndexOf(':');
                if (colon != -1)
                {
                    prefixPart = tag[..colon];
                }
                prefixPart = prefixPart.Split('/')[0];

                string prefixName = prefixPart;
                if (prefixName.EndsWith(']') && prefixName.Contains('['))
                {
                    int open = prefixName.LastIndexOf('[');
                    if (open != -1)
                    {
                        prefixName = prefixName[..open];
                    }
                }

                string tagPrefixLower = prefixName.ToLowerInvariant();
                bool isEditTag = tagPrefixLower == "edit";

                if (isEditTag)
                {
                    // Enter an edit section (any <edit...>)
                    inAnyEditSection = true;
                    continue; // drop tag + its content
                }

                if (inAnyEditSection)
                {
                    // End edit section when another "section" tag begins.
                    if (sectionEndingTagsLocal.Contains(tagPrefixLower))
                    {
                        inAnyEditSection = false;
                    }
                    else
                    {
                        continue; // drop anything inside edit section
                    }
                }

                // Not in edit section: keep tag+content verbatim
                resultLocal += "<" + piece;
            }

            return resultLocal.Trim();
        }

        string result = "";
        string[] pieces = prompt.Split('<');
        bool inWantedSection = false;
        bool sawRelevantEditTag = false;

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

                if (wantThisSection)
                {
                    sawRelevantEditTag = true;
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

        // If there is no <edit> / <edit[n]> that applies to this stage, fall back to the global prompt.
        // This is intentionally "tag presence" based: an explicitly-empty <edit> section should still win.
        if (!sawRelevantEditTag)
        {
            return RemoveAllEditSections(prompt, sectionEndingTags);
        }

        return result.Trim();
    }
}
