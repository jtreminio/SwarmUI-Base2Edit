using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Image = SwarmUI.Utils.Image;

namespace Base2Edit;

/// <summary>
/// Handles the execution of a single edit stage: model/VAE resolution, prompt conditioning,
/// sampling, and output finalization. Instantiated by <see cref="EditStage"/> per generation run.
/// </summary>
class StageRunner(WorkflowGenerator g, StageRefStore store)
{
    private const int PreEditImageSaveId = 50200;
    private const int EditSeedOffset = 2;

    private WGNodeData WrapLatent(JArray path) => new(path, g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
    private WGNodeData WrapImage(JArray path) => new(path, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
    private WGNodeData WrapVae(JArray path) => new(path, g, WGNodeData.DT_VAE, g.CurrentCompat());

    /// <summary>
    /// Core execution for a single edit stage. Resolves the model/VAE stack, parses prompts,
    /// ensures there's a latent to work with (re-encoding from image if the VAE changed),
    /// builds conditioning, runs the sampler, and decodes the result. On the final step it
    /// also rewires any downstream consumers (upscaler chains, etc.) so they pick up the
    /// post-edit image instead of the pre-edit one.
    /// </summary>
    public void RunStage(
        bool isFinalStep,
        int stageIndex,
        RunEditStageOptions options)
    {
        string positivePrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negativePrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositivePrompt = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.Prompt.Type.ID, positivePrompt);
        string originalNegativePrompt = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.NegativePrompt.Type.ID, negativePrompt);

        var prompts = new PromptParser.EditPrompts(
            PromptParser.ExtractPrompt(positivePrompt, originalPositivePrompt, stageIndex),
            PromptParser.ExtractPrompt(negativePrompt, originalNegativePrompt, stageIndex)
        );
        var modelState = PrepareModelAndVae(isFinalStep, stageIndex, options.TrackResolvedModelForMetadata);
        bool shouldSavePreEdit = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(Base2EditExtension.KeepPreEditImage, false);
        string preEditSaveNodeId = g.GetStableDynamicID(PreEditImageSaveId, stageIndex);
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        WGNodeData currentImageOut = g.CurrentMedia?.IsRawMedia == true ? g.CurrentMedia : null;
        bool needsPreEditImage = shouldSavePreEdit || modelState.MustReencode || currentSamples is null;
        var preEditImageTailRef = currentImageOut?.Path;
        JArray preEditConsumerSourceRef = preEditImageTailRef;
        if (isFinalStep && currentSamples is not null)
        {
            IReadOnlyList<WorkflowNode> preEditDecodes = WorkflowUtils.FindVaeDecodesBySamples(g.Workflow, currentSamples.Path);
            if (preEditDecodes.Count > 0)
            {
                WorkflowNode decode = preEditDecodes[0];
                preEditConsumerSourceRef = new JArray(decode.Id, 0);
            }
        }

        if (needsPreEditImage)
        {
            EnsureImageAvailable(modelState.PreEditVae);
        }

        if (shouldSavePreEdit)
        {
            SavePreEditImageIfNeeded(stageIndex);
        }

        if (!modelState.MustReencode && !options.ForceReencodeFromCurrentImage
            && currentSamples is not null
            && g.CurrentMedia?.IsLatentData != true)
        {
            g.CurrentMedia = WrapLatent(currentSamples.Path);
        }

        ReencodeIfNeeded(modelState, new ReencodeOptions(
            ForceFromCurrentImage: options.ForceReencodeFromCurrentImage
        ));
        (int stageWidth, int stageHeight) = ApplyEditUpscaleIfNeeded(modelState.Vae);
        var editParams = new Parameters(
            Width: stageWidth,
            Height: stageHeight,
            Steps: g.UserInput.Get(Base2EditExtension.EditSteps, 20),
            CfgScale: g.UserInput.Get(Base2EditExtension.EditCFGScale, 7.0),
            Control: g.UserInput.Get(Base2EditExtension.EditControl, 1.0),
            RefineOnly: g.UserInput.Get(Base2EditExtension.EditRefineOnly, false),
            Guidance: g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1),
            Seed: g.UserInput.Get(T2IParamTypes.Seed) + EditSeedOffset + stageIndex,
            Sampler: g.UserInput.Get(Base2EditExtension.EditSampler, "euler"),
            Scheduler: g.UserInput.Get(Base2EditExtension.EditScheduler, "normal")
        );
        var conditioning = CreateConditioning(modelState.Clip, prompts, editParams, stageIndex, modelState.Vae);
        ExecuteSampler(modelState.Model, conditioning, editParams, stageIndex);

        if (modelState.Vae is not null && g.CurrentCompat() is not null)
        {
            g.CurrentVae = WrapVae(modelState.Vae);
        }

        FinalizeOutput(modelState.Vae, isFinalStep, options.AllowFinalDecodeRetarget);

        if (isFinalStep && options.RewireFinalConsumers)
        {
            RewireDownstreamConsumers(preEditConsumerSourceRef, preEditImageTailRef, preEditSaveNodeId);
        }
    }

    /// <summary>
    /// Final-step edit runs late in the workflow. If earlier extensions already wired image consumers
    /// (eg postprocess/upscaler chains), repoint them from pre-edit image to post-edit image.
    /// </summary>
    private void RewireDownstreamConsumers(
        JArray preEditConsumerSourceRef,
        JArray preEditImageTailRef,
        string preEditSaveNodeId)
    {
        WGNodeData postEditImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        if (preEditConsumerSourceRef is null
            || postEditImageOut is null
            || JToken.DeepEquals(preEditConsumerSourceRef, postEditImageOut.Path))
        {
            return;
        }

        string postEditImageNodeId = $"{postEditImageOut.Path[0]}";
        int rewired = WorkflowUtils.RetargetInputConnections(
            g.Workflow,
            preEditConsumerSourceRef,
            postEditImageOut.Path,
            conn =>
            {
                if (g.Workflow[conn.NodeId] is not JObject node)
                {
                    return true;
                }
                string classType = $"{node["class_type"]}";
                if (classType == NodeTypes.SaveImage || classType == NodeTypes.SwarmSaveImageWS)
                {
                    return conn.NodeId != preEditSaveNodeId;
                }

                return !WorkflowUtils.IsNodeReachableFromNode(g.Workflow, conn.NodeId, postEditImageNodeId);
            });

        if (rewired == 0)
        {
            return;
        }

        // If we rewired an existing downstream branch (eg upscaler chain),
        // keep that branch endpoint as the final output so later save nodes attach there.
        if (preEditImageTailRef is not null
            && WorkflowUtils.IsNodeReachableFromNode(g.Workflow, postEditImageNodeId, $"{preEditImageTailRef[0]}"))
        {
            g.CurrentMedia = WrapImage(preEditImageTailRef);
        }
        else if (preEditImageTailRef is null
            && TryResolveUniqueDownstreamImageTail(postEditImageOut.Path, out JArray inferredTail))
        {
            g.CurrentMedia = WrapImage(inferredTail);
        }

        // Remove orphaned pre-edit decode node if nothing else consumes it
        if (preEditConsumerSourceRef.Count == 2
            && g.Workflow.TryGetValue($"{preEditConsumerSourceRef[0]}", out JToken sourceTok)
            && sourceTok is JObject sourceNode)
        {
            string classType = $"{sourceNode["class_type"]}";
            if ((classType == NodeTypes.VAEDecode || classType == NodeTypes.VAEDecodeTiled)
                && WorkflowUtils.FindInputConnections(g.Workflow, preEditConsumerSourceRef).Count == 0)
            {
                _ = g.Workflow.Remove($"{preEditConsumerSourceRef[0]}");
            }
        }
    }

    /// <summary>
    /// Sweeps the workflow for VAEDecode/VAEDecodeTiled nodes that nothing downstream consumes.
    /// These can pile up when edit stages retarget or replace decode nodes. Keeps the node
    /// backing the current image output so we don't break the final pipeline.
    /// </summary>
    internal void CleanupDanglingVaeDecodeNodes()
    {
        WGNodeData currentImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        string keepNodeId = currentImageOut is not null ? $"{currentImageOut.Path[0]}" : null;
        List<WorkflowNode> candidates =
        [
            .. WorkflowUtils.NodesOfType(g.Workflow, NodeTypes.VAEDecode),
            .. WorkflowUtils.NodesOfType(g.Workflow, NodeTypes.VAEDecodeTiled)
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

    /// <summary>
    /// Walks forward from an image output through the workflow graph, following single-consumer
    /// image connections, to find the tail end of a linear image chain (e.g. the last node in
    /// an upscaler pipeline). Returns false if the chain branches or loops. Save nodes are
    /// ignored so they don't cut the walk short.
    /// </summary>
    private bool TryResolveUniqueDownstreamImageTail(JArray startImageRef, out JArray tailImageRef)
    {
        tailImageRef = startImageRef;
        if (startImageRef is null || startImageRef.Count != 2)
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
                if (classType == NodeTypes.SaveImage || classType == NodeTypes.SwarmSaveImageWS)
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

    /// <summary>
    /// Figures out which model, CLIP, and VAE this edit stage should use. Handles "(Use Base)",
    /// "(Use Refiner)", and explicit model selections. Also applies any edit-specific VAE
    /// override and LoRA stack. Sets mustReencode when the VAE changed from what produced the
    /// current latent, so the caller knows to decode-then-reencode before sampling.
    /// </summary>
    private ModelState PrepareModelAndVae(
        bool isFinalStep,
        int stageIndex,
        bool trackResolvedModelForMetadata)
    {
        JArray preEditVae = g.CurrentVae.Path;
        JArray model = g.CurrentModel.Path;
        JArray clip = g.CurrentTextEnc.Path;
        JArray vae = g.CurrentVae.Path;
        T2IModel editModel = ModelPrep.TryResolveEditModel(g, Base2EditExtension.EditModel, out var mustReencode);

        if (editModel is null)
        {
            return new ModelState(model, clip, vae, preEditVae, mustReencode);
        }

        if (trackResolvedModelForMetadata)
        {
            g.UserInput.Set(Base2EditExtension.EditModelResolvedForMetadata, editModel);
        }

        string selection = g.UserInput.Get(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        int stageSectionId = Base2EditExtension.EditSectionIdForStage(stageIndex);

        (model, clip, vae) = ResolveModelStack(selection, editModel, isFinalStep, stageSectionId, model, clip, vae);
        (vae, mustReencode) = ResolveVae(selection, isFinalStep, vae, mustReencode);
        (model, clip) = ApplyLoraStack(stageIndex, stageSectionId, model, clip);

        if (mustReencode
            && preEditVae is not null
            && vae is not null
            && JToken.DeepEquals(preEditVae, vae))
        {
            mustReencode = false;
        }

        return new ModelState(model, clip, vae, preEditVae, mustReencode);
    }

    /// <summary>
    /// Resolves the model/clip/vae triplet for this stage. "(Use Base)" and "(Use Refiner)"
    /// pull from the captured pipeline state so they inherit any LoRAs applied during those
    /// phases. An explicit model name loads fresh and only gets global UI LoRAs.
    /// </summary>
    private (JArray model, JArray clip, JArray vae) ResolveModelStack(
        string selection,
        T2IModel editModel,
        bool isFinalStep,
        int stageSectionId,
        JArray model,
        JArray clip,
        JArray vae)
    {
        // "(Use Base)" and "(Use Refiner)" inherit the stage's model+LoRAs.
        // An explicit model name loads that model with only global UI LoRAs.
        if (string.Equals(selection, ModelPrep.UseBase, StringComparison.OrdinalIgnoreCase))
        {
            if (store.TryGetCapturedModelState(StageRefStore.StageKind.Base, out JArray baseModel, out JArray baseClip, out JArray baseVae))
            {
                return (baseModel, baseClip, baseVae);
            }
        }
        else if (string.Equals(selection, ModelPrep.UseRefiner, StringComparison.OrdinalIgnoreCase))
        {
            if (isFinalStep && store.TryGetCapturedModelState(StageRefStore.StageKind.Refiner, out JArray refinerModel, out JArray refinerClip, out JArray refinerVae))
            {
                return (refinerModel, refinerClip, refinerVae);
            }

            if (!isFinalStep)
            {
                (model, clip, vae) = ModelPrep.LoadEditModelWithoutLoras(g, editModel, sectionId: stageSectionId);
                (model, clip) = g.LoadLorasForConfinement(-1, model, clip);
                (model, clip) = g.LoadLorasForConfinement(0, model, clip);
                (model, clip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Refiner, model, clip);
                return (model, clip, vae);
            }
        }
        else
        {
            (model, clip, vae) = ModelPrep.LoadEditModelWithoutLoras(g, editModel, sectionId: stageSectionId);
            (model, clip) = g.LoadLorasForConfinement(-1, model, clip);
            (model, clip) = g.LoadLorasForConfinement(0, model, clip);
            return (model, clip, vae);
        }

        return (model, clip, vae);
    }

    /// <summary>
    /// Checks for VAE overrides: first the refiner's VAE when using the refiner model before
    /// the refiner phase, then any explicit edit VAE override. When a different VAE is loaded,
    /// flags mustReencode so the latent gets re-encoded through the new VAE before sampling.
    /// </summary>
    private (JArray vae, bool mustReencode) ResolveVae(
        string selection,
        bool isFinalStep,
        JArray vae,
        bool mustReencode)
    {
        // Inherit refiner VAE override when using refiner model before the refiner phase
        if (!isFinalStep
            && string.Equals(selection, ModelPrep.UseRefiner, StringComparison.OrdinalIgnoreCase)
            && !g.UserInput.TryGet(Base2EditExtension.EditVAE, out _)
            && g.UserInput.TryGet(T2IParamTypes.RefinerVAE, out T2IModel refinerVaeOverride)
            && refinerVaeOverride is not null)
        {
            vae = g.CreateVAELoader(refinerVaeOverride.ToString(g.ModelFolderFormat));
            g.CurrentVae = WrapVae(vae);
            return (vae, true);
        }

        // Explicit edit VAE override
        if (g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel altEditVae)
            && altEditVae is not null
            && altEditVae.Name != "Automatic")
        {
            vae = g.CreateVAELoader(altEditVae.ToString(g.ModelFolderFormat));
            g.CurrentVae = WrapVae(vae);
            return (vae, true);
        }

        return (vae, mustReencode);
    }

    /// <summary>
    /// Loads any LoRAs that were declared inside &lt;edit&gt; or &lt;edit[n]&gt; prompt sections for this
    /// stage. Temporarily replaces the generator's LoRA params with just the relevant ones,
    /// loads them confined to the stage's section ID, then restores the original LoRA state.
    /// </summary>
    private (JArray model, JArray clip) ApplyLoraStack(
        int stageIndex,
        int stageSectionId,
        JArray model,
        JArray clip)
    {
        string posPrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        bool hasStageEditSection =
            PromptParser.HasAnyEditSectionForStage(posPrompt, stageIndex)
            || PromptParser.HasAnyEditSectionForStage(negPrompt, stageIndex);

        if (!hasStageEditSection)
        {
            return (model, clip);
        }

        (List<string> Loras, List<string> Weights, List<string> TencWeights) loras = ExtractPromptLoras(stageIndex);
        if (loras.Loras.Count == 0)
        {
            return (model, clip);
        }

        ParamSnapshot snapshot = ModelPrep.SnapshotLoraParams(g);
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
            snapshot.Restore();
        }

        return (model, clip);
    }

    /// <summary>
    /// Filters the global LoRA list down to just the ones confined to this edit stage.
    /// Matches both the global edit confinement ID (applies to all stages) and the
    /// stage-specific confinement ID.
    /// </summary>
    private (List<string> Loras, List<string> Weights, List<string> TencWeights) ExtractPromptLoras(int stageIndex)
    {
        if (!g.UserInput.TryGet(T2IParamTypes.Loras, out List<string> loras)
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

    /// <summary>
    /// Makes sure there's a decoded image in the pipeline. If the current media is already an
    /// image, nothing to do. Otherwise tries to reuse an existing VAEDecode for the current
    /// samples (avoids duplicates), and as a last resort emits a new decode node.
    /// </summary>
    private void EnsureImageAvailable(JArray preEditVae)
    {
        if (g.CurrentMedia?.IsRawMedia == true)
        {
            return;
        }

        // If a prior stage already created a decode node for the current samples,
        // reuse it instead of emitting a duplicate VAEDecode.
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (currentSamples is not null && VaeNodeReuse.ReuseVaeDecodeForSamples(g, currentSamples.Path, out JArray reusedImage))
        {
            g.CurrentMedia = WrapImage(reusedImage);
            return;
        }

        if (currentSamples is null)
        {
            return;
        }

        g.CurrentMedia = WrapLatent(currentSamples.Path)
            .DecodeLatents(WrapVae(preEditVae), false);
    }

    private void SavePreEditImageIfNeeded(int stageIndex)
    {
        bool shouldSave = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || g.UserInput.Get(Base2EditExtension.KeepPreEditImage, false);

        WGNodeData currentImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        if (shouldSave && currentImageOut is not null)
        {
            string preEditSaveNodeId = g.GetStableDynamicID(PreEditImageSaveId, stageIndex);
            if (!g.Workflow.ContainsKey(preEditSaveNodeId))
            {
                WrapImage(currentImageOut.Path).SaveOutput(null, null, id: preEditSaveNodeId);
            }
            Logs.Debug("Base2Edit: Saved pre-edit image");
        }
    }

    /// <summary>
    /// Handles the decode→re-encode dance when the edit stage's VAE differs from what produced
    /// the current latent. Also covers the forced-from-current-image case (e.g. segment-after-
    /// refiner workflows). Tries to reuse existing VAEEncode nodes before creating new ones.
    /// </summary>
    private void ReencodeIfNeeded(ModelState modelState, ReencodeOptions options = default)
    {
        WGNodeData currentImageOut = g.CurrentMedia?.IsRawMedia == true ? g.CurrentMedia : WGNodeDataUtil.TryGetCurrentImage(g);
        if (options.ForceFromCurrentImage && currentImageOut is not null)
        {
            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, modelState.Vae, out JArray imageTailSamples))
            {
                g.CurrentMedia = WrapLatent(imageTailSamples);
            }
            else
            {
                string forcedEncodeNode = g.CreateVAEEncode(modelState.Vae, currentImageOut.Path);
                g.CurrentMedia = WrapLatent([forcedEncodeNode, 0]);
            }
            return;
        }

        // When the current image was decoded from a different latent than what g.CurrentMedia
        // tracks (e.g. after RestoreParentPipelineState), we may need to re-encode through the
        // edit VAE. Reuse an existing VAEEncode for this image+VAE if one already exists.
        if (currentImageOut is not null &&
            VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, modelState.Vae, out JArray reusedSamples))
        {
            g.CurrentMedia = WrapLatent(reusedSamples);
            return;
        }

        if (!modelState.MustReencode && WGNodeDataUtil.TryGetCurrentLatent(g) is not null)
        {
            return;
        }

        if (currentImageOut is null)
        {
            return;
        }

        string encodeNode = g.CreateVAEEncode(modelState.Vae, currentImageOut.Path);
        g.CurrentMedia = WrapLatent([encodeNode, 0]);
    }

    /// <summary>
    /// Returns a latent reference suitable for the edit sampler's input. If there's already a
    /// current latent, uses that. Otherwise encodes the current image through the preferred VAE
    /// (reusing an existing encode node if one matches). Falls back to a hardcoded legacy node
    /// ID when nothing else is available.
    /// </summary>
    private JArray EnsureCurrentSamplesForEdit(JArray preferredVae)
    {
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (currentSamples is not null)
        {
            return currentSamples.Path;
        }

        WGNodeData currentImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        JArray vae = preferredVae ?? g.CurrentVae.Path;
        if (currentImageOut is null || vae is null)
        {
            // Keep legacy fallback behavior when no media anchor has been established yet.
            return ["10", 0];
        }

        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, vae, out JArray reusedSamples))
        {
            return reusedSamples;
        }

        string encodeNode = g.CreateVAEEncode(vae, currentImageOut.Path);
        return [encodeNode, 0];
    }

    /// <summary>
    /// Builds the positive and negative conditioning for the edit sampler. Parses any
    /// &lt;b2eimage[...]&gt; tags from the prompt, resolves them to latents, and chains them as
    /// ReferenceLatent nodes onto the positive conditioning. The current stage's own latent
    /// is always added as the final reference (unless RefineOnly is on, which skips all
    /// reference latents and just does a straight denoise).
    /// </summary>
    private Conditioning CreateConditioning(
        JArray clip,
        PromptParser.EditPrompts prompts,
        Parameters editParams,
        int stageIndex,
        JArray currentStageVae)
    {
        PromptParser.ImagePromptParseResult imageRefs = PromptParser.ParseImageTags(prompts, stageIndex);
        PromptParser.EditPrompts cleanedPrompts = imageRefs.Prompts;
        string posCondNode = CreateConditioningNode(clip, cleanedPrompts.Positive, editParams);
        JArray positiveConditioning = [posCondNode, 0];
        StageResolver resolver = new(g, store);

        if (!editParams.RefineOnly)
        {
            List<JArray> referencedLatents = resolver.ResolveImageLatents(
                imageRefs.References,
                currentStageVae,
                stageIndex
            );

            foreach (JArray referencedLatent in referencedLatents)
            {
                string referenceNode = g.CreateNode(NodeTypes.ReferenceLatent, new JObject()
                {
                    ["conditioning"] = positiveConditioning,
                    ["latent"] = referencedLatent
                });
                positiveConditioning = [referenceNode, 0];
            }

            JArray currentStageSamples = EnsureCurrentSamplesForEdit(currentStageVae);
            if (currentStageSamples is null)
            {
                Logs.Warning($"Base2Edit: Stage {stageIndex} has no latent anchor; skipping implicit current-stage ReferenceLatent.");
                return new Conditioning(positiveConditioning, [CreateConditioningNode(clip, cleanedPrompts.Negative, editParams), 0]);
            }

            string currentStageReferenceNode = g.CreateNode(NodeTypes.ReferenceLatent, new JObject()
            {
                ["conditioning"] = positiveConditioning,
                ["latent"] = currentStageSamples
            });
            positiveConditioning = [currentStageReferenceNode, 0];
        }
        else if (imageRefs.References.Count > 0)
        {
            Logs.Warning($"Base2Edit: Ignoring <b2eimage[...]> in stage {stageIndex}: Refine Only is enabled.");
        }
        string negCondNode = CreateConditioningNode(clip, cleanedPrompts.Negative, editParams);

        return new Conditioning(positiveConditioning, [negCondNode, 0]);
    }

    private string CreateConditioningNode(
        JArray clip,
        string prompt,
        Parameters editParams)
    {
        return g.CreateNode(NodeTypes.SwarmClipTextEncodeAdvanced, new JObject()
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

    /// <summary>
    /// Runs the actual KSampler node for this edit stage. Temporarily removes prompt images
    /// from the generator so they don't bleed into edit stages (base-stage prompt-image behavior
    /// stays untouched). The start step is derived from the control parameter — higher control
    /// means more of the original image is preserved.
    /// </summary>
    private void ExecuteSampler(
        JArray model,
        Conditioning conditioning,
        Parameters editParams,
        int stageIndex)
    {
        bool hadPromptImages = g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> promptImages);
        if (hadPromptImages)
        {
            // Keep base-stage prompt-image behavior untouched, but prevent edit samplers from
            // implicitly chaining prompt images unless the user explicitly requests <b2eimage[promptN]>.
            g.UserInput.Remove(T2IParamTypes.PromptImages);
        }

        try
        {
            int startStep = (int)Math.Round(editParams.Steps * (1 - editParams.Control));
            int stageSectionId = Base2EditExtension.EditSectionIdForStage(stageIndex);
            JArray currentStageSamples = EnsureCurrentSamplesForEdit(preferredVae: null);
            if (currentStageSamples is null)
            {
                throw new SwarmReadableErrorException("Base2Edit: No latent anchor is available for edit-stage sampling.");
            }
            string samplerNode = g.CreateKSampler(
                model,
                conditioning.Positive,
                conditioning.Negative,
                currentStageSamples,
                editParams.CfgScale,
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

            g.CurrentMedia = WrapLatent([samplerNode, 0]);
        }
        finally
        {
            if (hadPromptImages)
            {
                g.UserInput.Set(T2IParamTypes.PromptImages, promptImages);
            }
        }
    }

    /// <summary>
    /// Decodes the edit sampler's output back to an image on the final step. Tries three
    /// strategies in order: (1) retarget an existing but unconsumed VAEDecode to point at the
    /// new latent, (2) reuse a decode node that already matches the samples+VAE pair, (3) emit
    /// a fresh VAEDecode. Skipped for non-final steps since later stages will handle decoding.
    /// </summary>
    private void FinalizeOutput(JArray vae, bool isFinalStep, bool allowFinalDecodeRetarget)
    {
        if (!isFinalStep)
        {
            return;
        }

        // Common case: a pre-edit decode was already emitted by upstream steps but is still unused.
        // Retarget it to the post-edit latent to avoid leaving a dangling decode node.
        WGNodeData currentImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (allowFinalDecodeRetarget &&
            currentImageOut is not null &&
            currentSamples is not null &&
            VaeNodeReuse.TryRetargetUnconsumedVaeDecode(g, currentImageOut.Path, vae, currentSamples.Path, out JArray retargetedImage))
        {
            g.CurrentMedia = WrapImage(retargetedImage);
            return;
        }

        // If a decode node already exists for the current samples with the intended VAE,
        // reuse it instead of emitting a duplicate VAEDecode.
        if (currentSamples is not null && VaeNodeReuse.ReuseVaeDecodeForSamplesAndVae(g, currentSamples.Path, vae, out JArray reusedImage))
        {
            g.CurrentMedia = WrapImage(reusedImage);
            return;
        }

        if (currentSamples is not null)
        {
            g.CurrentMedia = WrapLatent(currentSamples.Path).DecodeLatents(WrapVae(vae), false);
        }
    }

    /// <summary>
    /// Applies edit-stage upscaling prior to sampling, mirroring refiner-upscale behavior:
    /// pixel upscaling uses ImageScale (or model upscaler for model-* methods), latent
    /// upscaling uses LatentUpscaleBy. Returns the effective stage resolution.
    /// </summary>
    private (int Width, int Height) ApplyEditUpscaleIfNeeded(JArray stageVae)
    {
        int baseWidth = Math.Max(g.CurrentMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int baseHeight = Math.Max(g.CurrentMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        double upscale = g.UserInput.Get(Base2EditExtension.EditUpscale, 1.0);
        string upscaleMethod = g.UserInput.Get(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
        bool doUpscale = upscale != 1 && !string.IsNullOrWhiteSpace(upscaleMethod);
        if (!doUpscale)
        {
            return (baseWidth, baseHeight);
        }

        int width = (int)Math.Round(baseWidth * upscale);
        int height = (int)Math.Round(baseHeight * upscale);
        width = Math.Max(16, (width / 16) * 16);
        height = Math.Max(16, (height / 16) * 16);

        if (upscaleMethod.StartsWith("pixel-", StringComparison.OrdinalIgnoreCase)
            || upscaleMethod.StartsWith("model-", StringComparison.OrdinalIgnoreCase))
        {
            EnsureImageAvailable(stageVae);
            WGNodeData decoded = WGNodeDataUtil.TryGetCurrentImage(g);
            if (decoded is null)
            {
                return (baseWidth, baseHeight);
            }

            JArray upscaledImageRef;
            if (upscaleMethod.StartsWith("pixel-", StringComparison.OrdinalIgnoreCase))
            {
                string pixelMethod = upscaleMethod["pixel-".Length..];
                string scaleNode = g.CreateNode(NodeTypes.ImageScale, new JObject()
                {
                    ["image"] = decoded.Path,
                    ["width"] = width,
                    ["height"] = height,
                    ["upscale_method"] = pixelMethod,
                    ["crop"] = "disabled"
                });
                upscaledImageRef = [scaleNode, 0];
            }
            else
            {
                string modelName = upscaleMethod["model-".Length..];
                string loader = g.CreateNode(NodeTypes.UpscaleModelLoader, new JObject()
                {
                    ["model_name"] = modelName
                });
                string modelUpscale = g.CreateNode(NodeTypes.ImageUpscaleWithModel, new JObject()
                {
                    ["upscale_model"] = new JArray(loader, 0),
                    ["image"] = decoded.Path
                });
                string fitScale = g.CreateNode(NodeTypes.ImageScale, new JObject()
                {
                    ["image"] = new JArray(modelUpscale, 0),
                    ["width"] = width,
                    ["height"] = height,
                    ["upscale_method"] = "lanczos",
                    ["crop"] = "disabled"
                });
                upscaledImageRef = [fitScale, 0];
            }

            g.CurrentMedia = WrapImage(upscaledImageRef);
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;

            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, upscaledImageRef, stageVae, out JArray reusedSamples))
            {
                g.CurrentMedia = WrapLatent(reusedSamples);
            }
            else
            {
                g.CurrentMedia = g.CurrentMedia.EncodeToLatent(WrapVae(stageVae));
            }
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;
            return (width, height);
        }

        if (upscaleMethod.StartsWith("latent-", StringComparison.OrdinalIgnoreCase))
        {
            WGNodeData latentMedia = WGNodeDataUtil.TryGetCurrentLatent(g);
            if (latentMedia is null)
            {
                WGNodeData imageMedia = WGNodeDataUtil.TryGetCurrentImage(g);
                if (imageMedia is null || stageVae is null)
                {
                    return (baseWidth, baseHeight);
                }

                if (VaeNodeReuse.ReuseVaeEncodeForImage(g, imageMedia.Path, stageVae, out JArray reusedLatent))
                {
                    latentMedia = WrapLatent(reusedLatent);
                }
                else
                {
                    string encodeNode = g.CreateVAEEncode(stageVae, imageMedia.Path);
                    latentMedia = WrapLatent([encodeNode, 0]);
                }
            }

            g.CurrentMedia = WrapLatent(latentMedia.Path);
            string latentMethod = upscaleMethod["latent-".Length..];
            string latentUpscale = g.CreateNode(NodeTypes.LatentUpscaleBy, new JObject()
            {
                ["samples"] = g.CurrentMedia.Path,
                ["upscale_method"] = latentMethod,
                ["scale_by"] = upscale
            });
            g.CurrentMedia = g.CurrentMedia.WithPath([latentUpscale, 0]);
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;
            return (width, height);
        }

        return (baseWidth, baseHeight);
    }
}
