using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Image = SwarmUI.Utils.Image;

namespace Base2Edit;

class StageRunner(WorkflowGenerator g, StageRefStore store)
{
    private const int PreEditImageSaveId = 50200;
    private const int EditSeedOffset = 2;

    private WGNodeData WrapLatent(JArray path) => new(path, g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
    private WGNodeData WrapImage(JArray path) => new(path, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
    private WGNodeData WrapVae(JArray path) => new(path, g, WGNodeData.DT_VAE, g.CurrentCompat());
    private WGNodeData WrapModel(JArray path) => new(path, g, WGNodeData.DT_MODEL, g.CurrentCompat());
    private WGNodeData WrapClip(JArray path) => new(path, g, WGNodeData.DT_TEXTENC, g.CurrentCompat());

    public void RunStage(
        bool isFinalStep,
        StageSpec stage,
        RunEditStageOptions options)
    {
        int stageIndex = stage.Id;
        EditStageContext ctx = new(
            stage,
            Base2EditExtension.EditSectionIdForStage(stageIndex),
            g.CurrentMedia,
            g.CurrentVae);
        string positivePrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negativePrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        string originalPositivePrompt = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.Prompt.Type.ID, positivePrompt);
        string originalNegativePrompt = PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.NegativePrompt.Type.ID, negativePrompt);

        PromptParser.EditPrompts prompts = new(
            PromptParser.ExtractPrompt(positivePrompt, originalPositivePrompt, stageIndex),
            PromptParser.ExtractPrompt(negativePrompt, originalNegativePrompt, stageIndex)
        );
        ctx.ModelState = PrepareModelAndVae(ctx, isFinalStep, options);
        bool shouldSavePreEdit = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || ctx.Stage.KeepPreEditImage;
        string preEditSaveNodeId = g.GetStableDynamicID(PreEditImageSaveId, stageIndex);
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        WGNodeData currentImageOut = g.CurrentMedia?.IsRawMedia == true ? g.CurrentMedia : null;
        bool needsPreEditImage = shouldSavePreEdit || ctx.ModelState.MustReencode || currentSamples is null;
        WGNodeData preEditImageTailRef = currentImageOut;
        WGNodeData preEditConsumerSourceRef = preEditImageTailRef;
        if (isFinalStep && currentSamples is not null)
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            if (bridge.ResolvePath(currentSamples.Path) is INodeOutput samplesOut)
            {
                foreach ((ComfyNode node, INodeInput input) in bridge.Graph.FindInputsConnectedTo(samplesOut))
                {
                    if (node is VAEDecodeNode
                        && (StringUtils.Equals(input.Name, "samples") || StringUtils.Equals(input.Name, "latent")))
                    {
                        preEditConsumerSourceRef = WrapImage([node.Id, 0]);
                        break;
                    }
                }
            }
        }

        if (needsPreEditImage)
        {
            EnsureImageAvailable(ctx.ModelState.PreEditVae);
        }

        if (shouldSavePreEdit)
        {
            SavePreEditImageIfNeeded(ctx);
        }

        if (!ctx.ModelState.MustReencode && !options.ForceReencodeFromCurrentImage
            && currentSamples is not null
            && g.CurrentMedia?.IsLatentData != true)
        {
            int? mediaWidth = g.CurrentMedia?.Width;
            int? mediaHeight = g.CurrentMedia?.Height;
            g.CurrentMedia = WrapLatent(currentSamples.Path);
            g.CurrentMedia.Width = mediaWidth;
            g.CurrentMedia.Height = mediaHeight;
        }

        ReencodeIfNeeded(ctx, new ReencodeOptions(
            ForceFromCurrentImage: options.ForceReencodeFromCurrentImage
        ));
        (int stageWidth, int stageHeight) = ApplyEditUpscaleIfNeeded(ctx);
        ctx.Parameters = new Parameters(
            Width: stageWidth,
            Height: stageHeight,
            Steps: ctx.Stage.Steps,
            CfgScale: ctx.Stage.CfgScale,
            Control: ctx.Stage.Control,
            RefineOnly: ctx.Stage.RefineOnly,
            Guidance: g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1),
            Seed: g.UserInput.Get(T2IParamTypes.Seed) + EditSeedOffset + stageIndex,
            Sampler: ctx.Stage.Sampler,
            Scheduler: ctx.Stage.Scheduler
        );
        ctx.Conditioning = CreateConditioning(ctx, prompts);
        ExecuteSampler(ctx);

        if (ctx.ModelState.Vae is not null && g.CurrentCompat() is not null)
        {
            g.CurrentVae = WrapVae(ctx.ModelState.Vae.Path);
        }

        FinalizeOutput(ctx, isFinalStep, options);

        if (isFinalStep && options.RewireFinalConsumers)
        {
            RewireDownstreamConsumers(preEditConsumerSourceRef, preEditImageTailRef, preEditSaveNodeId);
        }

        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.Width ??= stageWidth;
            g.CurrentMedia.Height ??= stageHeight;
        }
    }

    private void RewireDownstreamConsumers(
        WGNodeData preEditConsumerSourceRef,
        WGNodeData preEditImageTailRef,
        string preEditSaveNodeId)
    {
        WGNodeData postEditImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        if (preEditConsumerSourceRef is null
            || postEditImageOut is null
            || JToken.DeepEquals(preEditConsumerSourceRef.Path, postEditImageOut.Path))
        {
            return;
        }

        string postEditImageNodeId = $"{postEditImageOut.Path[0]}";

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput from = bridge.ResolvePath(preEditConsumerSourceRef.Path);
        INodeOutput to = bridge.ResolvePath(postEditImageOut.Path);
        if (from is null || to is null)
        {
            return;
        }

        int rewired = bridge.Graph.RetargetConnections(from, to, (node, _) =>
        {
            if (node is SaveImageNode or SwarmSaveImageWSNode)
            {
                return node.Id != preEditSaveNodeId;
            }
            return !IsReachableDownstream(bridge, node.Id, postEditImageNodeId);
        });

        if (rewired == 0)
        {
            return;
        }

        if (preEditImageTailRef is not null
            && IsReachableDownstream(bridge, postEditImageNodeId, $"{preEditImageTailRef.Path[0]}"))
        {
            g.CurrentMedia = preEditImageTailRef;
        }
        else if (preEditImageTailRef is null
            && TryResolveUniqueDownstreamImageTail(bridge, postEditImageOut.Path, out JArray inferredTail))
        {
            g.CurrentMedia = WrapImage(inferredTail);
        }

        ComfyNode orphanCandidate = bridge.Graph.GetNode($"{preEditConsumerSourceRef.Path[0]}");
        if (orphanCandidate is VAEDecodeNode or VAEDecodeTiledNode
            && orphanCandidate.Outputs.Count > 0
            && bridge.Graph.FindInputsConnectedTo(orphanCandidate.Outputs[0]).Count == 0)
        {
            bridge.RemoveNode(orphanCandidate);
        }
    }

    private static bool IsReachableDownstream(WorkflowBridge bridge, string startNodeId, string targetNodeId)
    {
        if (string.IsNullOrWhiteSpace(startNodeId) || string.IsNullOrWhiteSpace(targetNodeId))
        {
            return false;
        }
        if (startNodeId == targetNodeId)
        {
            return true;
        }
        if (bridge.Graph.GetNode(startNodeId) is not ComfyNode start)
        {
            return false;
        }

        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [startNodeId];
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            foreach (INodeOutput output in current.Outputs)
            {
                foreach (ComfyNode consumer in bridge.Graph.FindDownstream(output))
                {
                    if (!visited.Add(consumer.Id))
                    {
                        continue;
                    }
                    if (consumer.Id == targetNodeId)
                    {
                        return true;
                    }
                    pending.Enqueue(consumer);
                }
            }
        }
        return false;
    }

    internal void CleanupDanglingVaeDecodeNodes()
    {
        WGNodeData currentImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        string keepNodeId = currentImageOut is not null ? $"{currentImageOut.Path[0]}" : null;

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        List<ComfyNode> candidates =
        [
            .. bridge.Graph.NodesOfType<VAEDecodeNode>(),
            .. bridge.Graph.NodesOfType<VAEDecodeTiledNode>()
        ];

        foreach (ComfyNode node in candidates)
        {
            if (!string.IsNullOrWhiteSpace(keepNodeId) && node.Id == keepNodeId)
            {
                continue;
            }
            if (node.Outputs.Count == 0)
            {
                continue;
            }
            if (bridge.Graph.FindInputsConnectedTo(node.Outputs[0]).Count == 0)
            {
                bridge.RemoveNode(node);
            }
        }
    }

    private static bool TryResolveUniqueDownstreamImageTail(
        WorkflowBridge bridge,
        JArray startImageRef,
        out JArray tailImageRef)
    {
        tailImageRef = startImageRef;
        if (startImageRef is null || startImageRef.Count != 2)
        {
            return false;
        }

        static bool IsImageFlowInputName(string inputName) =>
            inputName == "image" || inputName == "images" || inputName == "image_pass";

        if (bridge.ResolvePath(startImageRef) is not INodeOutput startOutput)
        {
            return false;
        }

        HashSet<string> visited = [];
        INodeOutput current = startOutput;

        while (true)
        {
            string currentKey = $"{current.Node.Id}:{current.SlotIndex}";
            if (!visited.Add(currentKey))
            {
                return false;
            }

            List<ComfyNode> imageConsumers = [];
            foreach ((ComfyNode node, INodeInput input) in bridge.Graph.FindInputsConnectedTo(current))
            {
                if (!IsImageFlowInputName(input.Name))
                {
                    continue;
                }
                if (node is SaveImageNode or SwarmSaveImageWSNode)
                {
                    continue;
                }
                imageConsumers.Add(node);
            }

            if (imageConsumers.Count == 0)
            {
                tailImageRef = new JArray(current.Node.Id, current.SlotIndex);
                return true;
            }
            if (imageConsumers.Count > 1)
            {
                tailImageRef = new JArray(current.Node.Id, current.SlotIndex);
                return false;
            }

            ComfyNode nextNode = imageConsumers[0];
            if (nextNode.Outputs.Count == 0)
            {
                tailImageRef = new JArray(nextNode.Id, 0);
                return false;
            }
            current = nextNode.Outputs[0];
        }
    }
    private ModelState PrepareModelAndVae(
        EditStageContext ctx,
        bool isFinalStep,
        RunEditStageOptions options)
    {
        int stageIndex = ctx.Stage.Id;
        bool trackResolvedModelForMetadata = options.TrackResolvedModelForMetadata;
        WGNodeData preEditVae = g.CurrentVae;
        WGNodeData model = g.CurrentModel;
        WGNodeData clip = g.CurrentTextEnc;
        WGNodeData vae = g.CurrentVae;
        T2IModel editModel = ModelPrep.TryResolveEditModel(g, ctx.Stage.Model ?? ModelPrep.UseRefiner, out bool mustReencode);

        if (editModel is null)
        {
            return new ModelState(model, clip, vae, preEditVae, mustReencode);
        }

        if (trackResolvedModelForMetadata)
        {
            g.UserInput.Set(Base2EditExtension.EditModelResolvedForMetadata, editModel);
        }

        string selection = ctx.Stage.Model ?? ModelPrep.UseRefiner;
        int stageSectionId = ctx.SectionId;

        (model, clip, vae) = ResolveModelStack(selection, editModel, isFinalStep, stageSectionId, model, clip, vae);
        (vae, mustReencode) = ResolveVae(selection, isFinalStep, stageSectionId, vae, mustReencode);
        (model, clip) = ApplyLoraStack(stageIndex, stageSectionId, model, clip);

        if (mustReencode
            && preEditVae is not null
            && vae is not null
            && JToken.DeepEquals(preEditVae.Path, vae.Path))
        {
            mustReencode = false;
        }

        return new ModelState(model, clip, vae, preEditVae, mustReencode);
    }

    private (WGNodeData model, WGNodeData clip, WGNodeData vae) ResolveModelStack(
        string selection,
        T2IModel editModel,
        bool isFinalStep,
        int stageSectionId,
        WGNodeData model,
        WGNodeData clip,
        WGNodeData vae)
    {
        if (StringUtils.Equals(selection, ModelPrep.UseBase))
        {
            if (store.GetCapturedModelState(StageRefStore.StageKind.Base) is { } baseState)
            {
                return (baseState.Model, baseState.Clip, baseState.Vae);
            }
        }
        else if (StringUtils.Equals(selection, ModelPrep.UseRefiner))
        {
            if (isFinalStep && store.GetCapturedModelState(StageRefStore.StageKind.Refiner) is { } refinerState)
            {
                return (refinerState.Model, refinerState.Clip, refinerState.Vae);
            }

            if (!isFinalStep)
            {
                ModelPrep.ModelRef modelRef = ModelPrep.LoadEditModelWithoutLoras(g, editModel, sectionId: stageSectionId);
                model = modelRef.Model;
                clip = modelRef.Clip;
                vae = modelRef.Vae;
                (JArray mArray, JArray cArray) = g.LoadLorasForConfinement(-1, model.Path, clip.Path);
                model = WrapModel(mArray);
                clip = WrapClip(cArray);
                (mArray, cArray) = g.LoadLorasForConfinement(0, model.Path, clip.Path);
                model = WrapModel(mArray);
                clip = WrapClip(cArray);
                (mArray, cArray) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Refiner, model.Path, clip.Path);
                model = WrapModel(mArray);
                clip = WrapClip(cArray);
                return (model, clip, vae);
            }
        }
        else
        {
            ModelPrep.ModelRef modelRef = ModelPrep.LoadEditModelWithoutLoras(g, editModel, sectionId: stageSectionId);
            model = modelRef.Model;
            clip = modelRef.Clip;
            vae = modelRef.Vae;
            (JArray mArray, JArray cArray) = g.LoadLorasForConfinement(-1, model.Path, clip.Path);
            model = WrapModel(mArray);
            clip = WrapClip(cArray);
            (mArray, cArray) = g.LoadLorasForConfinement(0, model.Path, clip.Path);
            model = WrapModel(mArray);
            clip = WrapClip(cArray);
            return (model, clip, vae);
        }

        return (model, clip, vae);
    }

    private (WGNodeData vae, bool mustReencode) ResolveVae(
        string selection,
        bool isFinalStep,
        int stageSectionId,
        WGNodeData vae,
        bool mustReencode)
    {
        if (!isFinalStep
            && StringUtils.Equals(selection, ModelPrep.UseRefiner)
            && !g.UserInput.TryGet(Base2EditExtension.EditVAE, out _, sectionId: stageSectionId)
            && g.UserInput.TryGet(T2IParamTypes.RefinerVAE, out T2IModel refinerVaeOverride)
            && refinerVaeOverride is not null)
        {
            JArray vaeArray = g.CreateVAELoader(refinerVaeOverride.ToString(g.ModelFolderFormat));
            vae = WrapVae(vaeArray);
            g.CurrentVae = vae;
            return (vae, true);
        }

        if (g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel altEditVae, sectionId: stageSectionId)
            && altEditVae is not null
            && altEditVae.Name != "Automatic")
        {
            JArray vaeArray = g.CreateVAELoader(altEditVae.ToString(g.ModelFolderFormat));
            vae = WrapVae(vaeArray);
            g.CurrentVae = vae;
            return (vae, true);
        }

        return (vae, mustReencode);
    }

    private (WGNodeData model, WGNodeData clip) ApplyLoraStack(
        int stageIndex,
        int stageSectionId,
        WGNodeData model,
        WGNodeData clip)
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

        using (ParamSnapshot snapshot = ModelPrep.SnapshotLoraParams(g))
        {
            snapshot.Remove();
            List<string> confinements = [.. Enumerable.Repeat($"{stageSectionId}", loras.Loras.Count)];
            g.UserInput.Set(T2IParamTypes.Loras, loras.Loras);
            g.UserInput.Set(T2IParamTypes.LoraWeights, loras.Weights);
            g.UserInput.Set(T2IParamTypes.LoraTencWeights, loras.TencWeights);
            g.UserInput.Set(T2IParamTypes.LoraSectionConfinement, confinements);
            (JArray mArray, JArray cArray) = g.LoadLorasForConfinement(stageSectionId, model.Path, clip.Path);
            model = WrapModel(mArray);
            clip = WrapClip(cArray);
        }

        return (model, clip);
    }

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

    private void EnsureImageAvailable(WGNodeData preEditVae)
    {
        if (g.CurrentMedia?.IsRawMedia == true)
        {
            return;
        }

        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (currentSamples is not null && VaeNodeReuse.ReuseVaeDecodeForSamples(g, currentSamples.Path, out INodeOutput reusedImage))
        {
            g.CurrentMedia = WrapImage(WorkflowBridge.ToPath(reusedImage));
            return;
        }

        if (currentSamples is null)
        {
            return;
        }

        g.CurrentMedia = currentSamples.DecodeLatents(preEditVae, false);
    }

    private void SavePreEditImageIfNeeded(EditStageContext ctx)
    {
        int stageIndex = ctx.Stage.Id;
        bool shouldSave = g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)
            || ctx.Stage.KeepPreEditImage;

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

    private void ReencodeIfNeeded(EditStageContext ctx, ReencodeOptions options = default)
    {
        ModelState modelState = ctx.ModelState;
        WGNodeData currentImageOut = g.CurrentMedia?.IsRawMedia == true ? g.CurrentMedia : WGNodeDataUtil.TryGetCurrentImage(g);
        if (options.ForceFromCurrentImage && currentImageOut is not null)
        {
            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, modelState.Vae.Path, out INodeOutput imageTailSamples))
            {
                g.CurrentMedia = WrapLatent(WorkflowBridge.ToPath(imageTailSamples));
                g.CurrentMedia.Width = currentImageOut.Width;
                g.CurrentMedia.Height = currentImageOut.Height;
            }
            else
            {
                string forcedEncodeNode = g.CreateVAEEncode(modelState.Vae.Path, currentImageOut.Path);
                g.CurrentMedia = WrapLatent([forcedEncodeNode, 0]);
                g.CurrentMedia.Width = currentImageOut.Width;
                g.CurrentMedia.Height = currentImageOut.Height;
            }
            return;
        }

        if (currentImageOut is not null &&
            VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, modelState.Vae.Path, out INodeOutput reusedSamples))
        {
            g.CurrentMedia = WrapLatent(WorkflowBridge.ToPath(reusedSamples));
            g.CurrentMedia.Width = currentImageOut.Width;
            g.CurrentMedia.Height = currentImageOut.Height;
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

        string encodeNode = g.CreateVAEEncode(modelState.Vae.Path, currentImageOut.Path);
        g.CurrentMedia = WrapLatent([encodeNode, 0]);
        g.CurrentMedia.Width = currentImageOut.Width;
        g.CurrentMedia.Height = currentImageOut.Height;
    }

    private JArray EnsureCurrentSamplesForEdit(WGNodeData preferredVae)
    {
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (currentSamples is not null)
        {
            return currentSamples.Path;
        }

        WGNodeData currentImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        JArray vae = (preferredVae ?? g.CurrentVae).Path;
        if (currentImageOut is null || vae is null)
        {
            return ["10", 0];
        }

        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, vae, out INodeOutput reusedSamples))
        {
            return WorkflowBridge.ToPath(reusedSamples);
        }

        string encodeNode = g.CreateVAEEncode(vae, currentImageOut.Path);
        return [encodeNode, 0];
    }

    private Conditioning CreateConditioning(
        EditStageContext ctx,
        PromptParser.EditPrompts prompts)
    {
        WGNodeData clip = ctx.ModelState.Clip;
        Parameters editParams = ctx.Parameters;
        int stageIndex = ctx.Stage.Id;
        WGNodeData currentStageVae = ctx.ModelState.Vae;
        PromptParser.ImagePromptParseResult imageRefs = PromptParser.ParseImageTags(prompts, stageIndex);
        PromptParser.EditPrompts cleanedPrompts = imageRefs.Prompts;
        StageResolver resolver = new(g, store);

        List<JArray> referencedLatents = !editParams.RefineOnly
            ? resolver.ResolveImageLatents(imageRefs.References, currentStageVae.Path, stageIndex)
            : [];
        JArray currentStageSamples = !editParams.RefineOnly
            ? EnsureCurrentSamplesForEdit(currentStageVae)
            : null;

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        JArray positiveConditioning = [BuildPromptEncoder(bridge, clip, cleanedPrompts.Positive, editParams), 0];

        if (!editParams.RefineOnly)
        {
            foreach (JArray referencedLatent in referencedLatents)
            {
                positiveConditioning = [AddReferenceLatent(bridge, positiveConditioning, referencedLatent), 0];
            }

            if (currentStageSamples is null)
            {
                Logs.Warning($"Base2Edit: Stage {stageIndex} has no latent anchor; skipping implicit current-stage ReferenceLatent.");
                return new Conditioning(positiveConditioning, [BuildPromptEncoder(bridge, clip, cleanedPrompts.Negative, editParams), 0]);
            }

            positiveConditioning = [AddReferenceLatent(bridge, positiveConditioning, currentStageSamples), 0];
        }
        else if (imageRefs.References.Count > 0)
        {
            Logs.Warning($"Base2Edit: Ignoring <b2eimage[...]> in stage {stageIndex}: Refine Only is enabled.");
        }

        return new Conditioning(positiveConditioning, [BuildPromptEncoder(bridge, clip, cleanedPrompts.Negative, editParams), 0]);
    }

    private string BuildPromptEncoder(
        WorkflowBridge bridge,
        WGNodeData clip,
        string prompt,
        Parameters editParams)
    {
        SwarmClipTextEncodeAdvancedNode node = bridge.AddNode(
            new SwarmClipTextEncodeAdvancedNode().With(
                Steps: editParams.Steps,
                Prompt: prompt,
                Width: editParams.Width,
                Height: editParams.Height,
                TargetWidth: editParams.Width,
                TargetHeight: editParams.Height,
                Guidance: editParams.Guidance),
            id: $"{g.LastID++}");
        node.Clip.ConnectFromPath(bridge, clip.Path);
        return node.Id;
    }

    private string AddReferenceLatent(WorkflowBridge bridge, JArray conditioning, JArray latent)
    {
        ReferenceLatentNode node = bridge.AddNode(new ReferenceLatentNode(), id: $"{g.LastID++}");
        node.Conditioning.ConnectFromPath(bridge, conditioning);
        node.Latent.ConnectFromPath(bridge, latent);
        return node.Id;
    }

    private void ExecuteSampler(EditStageContext ctx)
    {
        WGNodeData model = ctx.ModelState.Model;
        Conditioning conditioning = ctx.Conditioning;
        Parameters editParams = ctx.Parameters;
        bool hadPromptImages = g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> promptImages);
        if (hadPromptImages)
        {
            g.UserInput.Remove(T2IParamTypes.PromptImages);
        }

        try
        {
            int startStep = (int)Math.Round(editParams.Steps * (1 - editParams.Control));
            int stageSectionId = ctx.SectionId;
            JArray currentStageSamples = EnsureCurrentSamplesForEdit(preferredVae: null);
            if (currentStageSamples is null)
            {
                throw new SwarmReadableErrorException("Base2Edit: No latent anchor is available for edit-stage sampling.");
            }
            string samplerNode = g.CreateKSampler(
                model.Path,
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
            g.CurrentMedia.Width = editParams.Width;
            g.CurrentMedia.Height = editParams.Height;
        }
        finally
        {
            if (hadPromptImages)
            {
                g.UserInput.Set(T2IParamTypes.PromptImages, promptImages);
            }
        }
    }

    private void FinalizeOutput(EditStageContext ctx, bool isFinalStep, RunEditStageOptions options)
    {
        if (!isFinalStep)
        {
            return;
        }

        WGNodeData vae = ctx.ModelState.Vae;
        bool allowFinalDecodeRetarget = options.AllowFinalDecodeRetarget;

        WGNodeData currentImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (allowFinalDecodeRetarget &&
            currentImageOut is not null &&
            currentSamples is not null &&
            VaeNodeReuse.TryRetargetUnconsumedVaeDecode(g, currentImageOut.Path, vae.Path, currentSamples.Path, out INodeOutput retargetedImage))
        {
            g.CurrentMedia = WrapImage(WorkflowBridge.ToPath(retargetedImage));
            return;
        }

        if (currentSamples is not null && VaeNodeReuse.ReuseVaeDecodeForSamplesAndVae(g, currentSamples.Path, vae.Path, out INodeOutput reusedImage))
        {
            g.CurrentMedia = WrapImage(WorkflowBridge.ToPath(reusedImage));
            return;
        }

        if (currentSamples is not null)
        {
            g.CurrentMedia = currentSamples.DecodeLatents(vae, false);
        }
    }

    private (int Width, int Height) ApplyEditUpscaleIfNeeded(EditStageContext ctx)
    {
        WGNodeData stageVae = ctx.ModelState.Vae;
        int baseWidth = Math.Max(g.CurrentMedia?.Width ?? g.UserInput.GetImageWidth(), 16);
        int baseHeight = Math.Max(g.CurrentMedia?.Height ?? g.UserInput.GetImageHeight(), 16);
        double upscale = ctx.Stage.Upscale;
        string upscaleMethod = ctx.Stage.UpscaleMethod ?? "pixel-lanczos";
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

            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            JArray upscaledImageRef;
            if (upscaleMethod.StartsWith("pixel-", StringComparison.OrdinalIgnoreCase))
            {
                string pixelMethod = upscaleMethod["pixel-".Length..];
                upscaledImageRef = [AddImageScale(bridge, decoded.Path, width, height, pixelMethod, "disabled"), 0];
            }
            else
            {
                string modelName = upscaleMethod["model-".Length..];
                string loader = AddUpscaleModelLoader(bridge, modelName);
                string modelUpscale = AddImageUpscaleWithModel(bridge, new JArray(loader, 0), decoded.Path);
                upscaledImageRef = [AddImageScale(bridge, new JArray(modelUpscale, 0), width, height, "lanczos", "disabled"), 0];
            }

            g.CurrentMedia = WrapImage(upscaledImageRef);
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;

            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, upscaledImageRef, stageVae.Path, out INodeOutput reusedSamples))
            {
                g.CurrentMedia = WrapLatent(WorkflowBridge.ToPath(reusedSamples));
            }
            else
            {
                g.CurrentMedia = g.CurrentMedia.EncodeToLatent(stageVae);
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

                if (VaeNodeReuse.ReuseVaeEncodeForImage(g, imageMedia.Path, stageVae.Path, out INodeOutput reusedLatent))
                {
                    latentMedia = WrapLatent(WorkflowBridge.ToPath(reusedLatent));
                }
                else
                {
                    string encodeNode = g.CreateVAEEncode(stageVae.Path, imageMedia.Path);
                    latentMedia = WrapLatent([encodeNode, 0]);
                }
            }

            g.CurrentMedia = WrapLatent(latentMedia.Path);
            string latentMethod = upscaleMethod["latent-".Length..];
            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            string latentUpscale = AddLatentUpscaleBy(bridge, g.CurrentMedia.Path, latentMethod, upscale);
            g.CurrentMedia = g.CurrentMedia.WithPath([latentUpscale, 0]);
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;
            return (width, height);
        }

        return (baseWidth, baseHeight);
    }

    private string AddImageScale(WorkflowBridge bridge, JArray imagePath, int width, int height, string method, string crop)
    {
        ImageScaleNode node = bridge.AddNode(
            new ImageScaleNode().With(
                Width: width,
                Height: height,
                UpscaleMethod: method,
                Crop: crop),
            id: $"{g.LastID++}");
        node.Image.ConnectFromPath(bridge, imagePath);
        return node.Id;
    }

    private string AddUpscaleModelLoader(WorkflowBridge bridge, string modelName)
    {
        UpscaleModelLoaderNode node = bridge.AddNode(
            new UpscaleModelLoaderNode().With(ModelName: modelName),
            id: $"{g.LastID++}");
        return node.Id;
    }

    private string AddImageUpscaleWithModel(WorkflowBridge bridge, JArray modelPath, JArray imagePath)
    {
        ImageUpscaleWithModelNode node = bridge.AddNode(
            new ImageUpscaleWithModelNode(),
            id: $"{g.LastID++}");
        node.UpscaleModel.ConnectFromPath(bridge, modelPath);
        node.Image.ConnectFromPath(bridge, imagePath);
        return node.Id;
    }

    private string AddLatentUpscaleBy(WorkflowBridge bridge, JArray samplesPath, string method, double scaleBy)
    {
        LatentUpscaleByNode node = bridge.AddNode(
            new LatentUpscaleByNode().With(
                UpscaleMethod: method,
                ScaleBy: scaleBy),
            id: $"{g.LastID++}");
        node.Samples.ConnectFromPath(bridge, samplesPath);
        return node.Id;
    }
}
