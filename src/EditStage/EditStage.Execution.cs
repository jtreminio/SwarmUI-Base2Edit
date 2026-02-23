using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public partial class EditStage
{
    private static void RunEditStage(
        WorkflowGenerator g,
        bool isFinalStep,
        int stageIndex,
        RunEditStageOptions options = default)
    {
        string positivePrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negativePrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositivePrompt = GetOriginalPrompt(g.UserInput, T2IParamTypes.Prompt.Type.ID, positivePrompt);
        string originalNegativePrompt = GetOriginalPrompt(g.UserInput, T2IParamTypes.NegativePrompt.Type.ID, negativePrompt);

        var prompts = new EditPrompts(
            ExtractPrompt(positivePrompt, originalPositivePrompt, stageIndex),
            ExtractPrompt(negativePrompt, originalNegativePrompt, stageIndex)
        );
        var modelState = PrepareEditModelAndVae(g, isFinalStep, stageIndex, options.TrackResolvedModelForMetadata);
        string modelSelection = g.UserInput.Get(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        bool shouldSavePreEdit = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(Base2EditExtension.KeepPreEditImage, false);
        string preEditSaveNodeId = g.GetStableDynamicID(PreEditImageSaveId, stageIndex);
        bool needsPreEditImage = shouldSavePreEdit || modelState.MustReencode || g.FinalSamples is null;
        JArray preEditImageTailRef = g.FinalImageOut is null ? null : new JArray(g.FinalImageOut[0], g.FinalImageOut[1]);
        JArray preEditConsumerSourceRef = preEditImageTailRef;
        if (isFinalStep && g.FinalSamples is not null)
        {
            IReadOnlyList<WorkflowNode> preEditDecodes = WorkflowUtils.FindVaeDecodesBySamples(g.Workflow, g.FinalSamples);
            if (preEditDecodes.Count > 0)
            {
                WorkflowNode decode = preEditDecodes[0];
                preEditConsumerSourceRef = new JArray(decode.Id, 0);
            }
        }

        if (needsPreEditImage)
        {
            EnsureImageAvailable(g, modelState.PreEditVae);
        }

        if (shouldSavePreEdit)
        {
            SavePreEditImageIfNeeded(g, stageIndex);
        }

        ReencodeIfNeeded(g, modelState, new ReencodeOptions(
            ForceFromCurrentImage: options.ForceReencodeFromCurrentImage
        ));
        var editParams = new EditParameters(
            Width: g.UserInput.GetImageWidth(),
            Height: g.UserInput.GetImageHeight(),
            Steps: g.UserInput.Get(Base2EditExtension.EditSteps, 20),
            Cfg: ResolveInheritedCfg(g, modelSelection),
            Control: g.UserInput.Get(Base2EditExtension.EditControl, 1.0),
            RefineOnly: g.UserInput.Get(Base2EditExtension.EditRefineOnly, false),
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

        FinalizeEditOutput(g, modelState.Vae, isFinalStep, options.AllowFinalDecodeRetarget);

        // Final-step edit runs late in the workflow. If earlier extensions already wired image consumers
        // (eg postprocess/upscaler chains), repoint them from pre-edit image to post-edit image.
        if (isFinalStep
            && options.RewireFinalConsumers
            && preEditConsumerSourceRef is not null
            && g.FinalImageOut is not null
            && !JToken.DeepEquals(preEditConsumerSourceRef, g.FinalImageOut))
        {
            string postEditImageNodeId = $"{g.FinalImageOut[0]}";
            int rewired = WorkflowUtils.RetargetInputConnections(
                g.Workflow,
                preEditConsumerSourceRef,
                g.FinalImageOut,
                conn =>
                {
                    if (g.Workflow?[conn.NodeId] is not JObject node)
                    {
                        return true;
                    }
                    string classType = $"{node["class_type"]}";
                    if (classType == "SaveImage" || classType == "SwarmSaveImageWS")
                    {
                        return conn.NodeId != preEditSaveNodeId;
                    }

                    // Avoid feedback loops by rewiring a consumer that already flows
                    // into the post-edit image branch (eg image->encode->sampler->decode).
                    return !WorkflowUtils.IsNodeReachableFromNode(g.Workflow, conn.NodeId, postEditImageNodeId);
                });

            // If we rewired an existing downstream branch (eg upscaler chain),
            // keep that branch endpoint as the final output so later save nodes attach there.
            if (rewired > 0 && preEditImageTailRef is not null
                && WorkflowUtils.IsNodeReachableFromNode(g.Workflow, postEditImageNodeId, $"{preEditImageTailRef[0]}"))
            {
                g.FinalImageOut = preEditImageTailRef;
            }
            else if (rewired > 0 && preEditImageTailRef is null
                && TryResolveUniqueDownstreamImageTail(g, g.FinalImageOut, out JArray inferredTail))
            {
                g.FinalImageOut = inferredTail;
            }

            if (rewired > 0
                && preEditConsumerSourceRef.Count == 2
                && g.Workflow.TryGetValue($"{preEditConsumerSourceRef[0]}", out JToken sourceTok)
                && sourceTok is JObject sourceNode)
            {
                string classType = $"{sourceNode["class_type"]}";
                if ((classType == "VAEDecode" || classType == "VAEDecodeTiled")
                    && WorkflowUtils.FindInputConnections(g.Workflow, preEditConsumerSourceRef).Count == 0)
                {
                    _ = g.Workflow.Remove($"{preEditConsumerSourceRef[0]}");
                }
            }
        }

    }

    private static void CleanupDanglingVaeDecodeNodes(WorkflowGenerator g)
    {
        if (g?.Workflow is null)
        {
            return;
        }

        string keepNodeId = g.FinalImageOut is not null ? $"{g.FinalImageOut[0]}" : null;
        List<WorkflowNode> candidates =
        [
            .. WorkflowUtils.NodesOfType(g.Workflow, "VAEDecode"),
            .. WorkflowUtils.NodesOfType(g.Workflow, "VAEDecodeTiled")
        ];

        foreach (WorkflowNode node in candidates)
        {
            if (node.Node is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(keepNodeId) && node.Id == keepNodeId)
            {
                continue;
            }

            JArray outRef = new(node.Id, 0);
            if (WorkflowUtils.FindInputConnections(g.Workflow, outRef).Count == 0)
            {
                _ = g.Workflow.Remove(node.Id);
            }
        }
    }

    private static bool TryResolveUniqueDownstreamImageTail(WorkflowGenerator g, JArray startImageRef, out JArray tailImageRef)
    {
        tailImageRef = startImageRef;
        if (g?.Workflow is null || startImageRef is null || startImageRef.Count != 2)
        {
            return false;
        }

        static bool IsImageFlowInputName(string inputName) =>
            inputName == "image" || inputName == "images" || inputName == "image_pass";

        HashSet<string> visited = [];
        JArray current = new(startImageRef[0], startImageRef[1]);

        while (true)
        {
            string currentKey = $"{current[0]}:{current[1]}";
            if (!visited.Add(currentKey))
            {
                return false;
            }

            List<WorkflowInputConnection> imageConsumers = [];
            foreach (WorkflowInputConnection conn in WorkflowUtils.FindInputConnections(g.Workflow, current))
            {
                if (!IsImageFlowInputName(conn.InputName))
                {
                    continue;
                }
                if (g.Workflow[conn.NodeId] is not JObject node)
                {
                    continue;
                }

                string classType = $"{node["class_type"]}";
                if (classType == "SaveImage" || classType == "SwarmSaveImageWS")
                {
                    continue;
                }
                imageConsumers.Add(conn);
            }

            if (imageConsumers.Count == 0)
            {
                tailImageRef = current;
                return true;
            }
            if (imageConsumers.Count > 1)
            {
                tailImageRef = current;
                return false;
            }

            WorkflowInputConnection onlyConsumer = imageConsumers[0];
            current = new JArray(onlyConsumer.NodeId, 0);
        }
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

    private static EditModelState PrepareEditModelAndVae(WorkflowGenerator g, bool isFinalStep, int stageIndex, bool trackResolvedModelForMetadata)
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
        if (trackResolvedModelForMetadata)
        {
            g.UserInput.Set(Base2EditExtension.EditModelResolvedForMetadata, editModel);
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

        if (shouldSave && g.FinalImageOut is not null)
        {
            string preEditSaveNodeId = g.GetStableDynamicID(PreEditImageSaveId, stageIndex);
            if (!g.Workflow.ContainsKey(preEditSaveNodeId))
            {
                g.CreateImageSaveNode(g.FinalImageOut, preEditSaveNodeId);
            }
            Logs.Debug("Base2Edit: Saved pre-edit image");
        }
    }

    private static void ReencodeIfNeeded(WorkflowGenerator g, EditModelState modelState, ReencodeOptions options = default)
    {
        if (options.ForceFromCurrentImage && g.FinalImageOut is not null)
        {
            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, g.FinalImageOut, modelState.Vae, out JArray imageTailSamples))
            {
                g.FinalSamples = imageTailSamples;
            }
            else
            {
                string forcedEncodeNode = g.CreateVAEEncode(modelState.Vae, g.FinalImageOut);
                g.FinalSamples = [forcedEncodeNode, 0];
            }
            return;
        }

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
        JArray positiveConditioning = [posCondNode, 0];
        if (!editParams.RefineOnly)
        {
            string refLatentNode = g.CreateNode("ReferenceLatent", new JObject()
            {
                ["conditioning"] = new JArray { posCondNode, 0 },
                ["latent"] = g.FinalSamples
            });
            positiveConditioning = [refLatentNode, 0];
        }
        string negCondNode = CreateConditioningNode(g, clip, prompts.Negative, editParams);

        return new EditConditioning(positiveConditioning, [negCondNode, 0]);
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

    private static void FinalizeEditOutput(WorkflowGenerator g, JArray vae, bool isFinalStep, bool allowFinalDecodeRetarget)
    {
        if (!isFinalStep)
        {
            g.FinalImageOut = null;
            return;
        }

        // Common case: a pre-edit decode was already emitted by upstream steps but is still unused.
        // Retarget it to the post-edit latent to avoid leaving a dangling decode node.
        if (allowFinalDecodeRetarget &&
            g.FinalImageOut is not null &&
            VaeNodeReuse.TryRetargetUnconsumedVaeDecode(g, g.FinalImageOut, vae, g.FinalSamples, out JArray retargetedImage))
        {
            g.FinalImageOut = retargetedImage;
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

}
