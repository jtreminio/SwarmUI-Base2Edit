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
        PromptParser.EditPrompts prompts = new(stage.PositivePrompt, stage.NegativePrompt);
        WGNodeData savedModel = g.CurrentModel;
        WGNodeData savedTextEnc = g.CurrentTextEnc;
        T2IModel savedFinalLoadedModel = g.FinalLoadedModel;
        List<T2IModel> savedFinalLoadedModelList = g.FinalLoadedModelList;
        g.FinalLoadedModelList = [.. savedFinalLoadedModelList];
        try
        {
            ctx.ModelState = PrepareModelAndVae(ctx, isFinalStep, options);
            g.CurrentModel = ctx.ModelState.Model;
            g.CurrentTextEnc = ctx.ModelState.Clip;
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
                            preEditConsumerSourceRef = new WGNodeData([node.Id, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
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
                g.CurrentMedia = new WGNodeData(currentSamples.Path, g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat())
                {
                    Width = mediaWidth,
                    Height = mediaHeight
                };
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
                Guidance: stage.Guidance,
                Seed: stage.Seed,
                Sampler: ctx.Stage.Sampler,
                Scheduler: ctx.Stage.Scheduler
            );
            ctx.Conditioning = CreateConditioning(ctx, prompts);
            ExecuteSampler(ctx);

            if (ctx.ModelState.Vae is not null && g.CurrentCompat() is not null)
            {
                g.CurrentVae = new WGNodeData(ctx.ModelState.Vae.Path, g, WGNodeData.DT_VAE, g.CurrentCompat());
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
        finally
        {
            g.CurrentModel = savedModel;
            g.CurrentTextEnc = savedTextEnc;
            g.FinalLoadedModel = savedFinalLoadedModel;
            g.FinalLoadedModelList = savedFinalLoadedModelList;
        }
    }

    private void RewireDownstreamConsumers(
        WGNodeData preEditConsumerSourceRef,
        WGNodeData preEditImageTailRef,
        string preEditSaveNodeId)
    {
        WGNodeData postEditImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);
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
            g.CurrentMedia = new WGNodeData(inferredTail, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
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
        WGNodeData currentImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);
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
        WGNodeData preEditVae = g.CurrentVae;
        WGNodeData model = g.CurrentModel;
        WGNodeData clip = g.CurrentTextEnc;
        WGNodeData vae = g.CurrentVae;
        T2IModel editModel = ctx.Stage.Model;

        if (editModel is null)
        {
            return new ModelState(model, clip, vae, preEditVae, MustReencode: false);
        }

        bool mustReencode = ModelPrep.RegisterAsFinalLoaded(g, editModel);

        if (options.TrackResolvedModelForMetadata)
        {
            g.UserInput.Set(Base2EditExtension.EditModelResolvedForMetadata, editModel);
        }

        int stageSectionId = ctx.SectionId;

        (model, clip, vae) = ResolveModelStack(ctx.Stage.ModelSource, editModel, isFinalStep, stageSectionId, model, clip, vae);
        (vae, mustReencode) = ResolveVae(ctx.Stage, isFinalStep, vae, mustReencode);
        (model, clip) = ApplyLoraStack(ctx.Stage, stageSectionId, model, clip);

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
        ModelSource source,
        T2IModel editModel,
        bool isFinalStep,
        int stageSectionId,
        WGNodeData model,
        WGNodeData clip,
        WGNodeData vae)
    {
        switch (source)
        {
            case ModelSource.Base:
                if (store.GetCapturedModelState(StageRefStore.StageKind.Base) is { } baseState)
                {
                    return (baseState.Model, baseState.Clip, baseState.Vae);
                }
                return (model, clip, vae);

            case ModelSource.Refiner:
                if (isFinalStep && store.GetCapturedModelState(StageRefStore.StageKind.Refiner) is { } refinerState)
                {
                    return (refinerState.Model, refinerState.Clip, refinerState.Vae);
                }
                if (isFinalStep)
                {
                    return (model, clip, vae);
                }
                return LoadFreshWithGlobalLoras(editModel, stageSectionId, includeRefinerConfinement: true);

            case ModelSource.Specific:
                return LoadFreshWithGlobalLoras(editModel, stageSectionId, includeRefinerConfinement: false);

            default:
                return (model, clip, vae);
        }
    }

    private (WGNodeData model, WGNodeData clip, WGNodeData vae) LoadFreshWithGlobalLoras(
        T2IModel editModel,
        int stageSectionId,
        bool includeRefinerConfinement)
    {
        ModelPrep.ModelRef modelRef = ModelPrep.LoadEditModelWithoutLoras(g, editModel, sectionId: stageSectionId);
        WGNodeData model = modelRef.Model;
        WGNodeData clip = modelRef.Clip;
        WGNodeData vae = modelRef.Vae;
        (model, clip) = ApplyConfinement(-1, model, clip);
        (model, clip) = ApplyConfinement(0, model, clip);
        if (includeRefinerConfinement)
        {
            (model, clip) = ApplyConfinement(T2IParamInput.SectionID_Refiner, model, clip);
        }
        return (model, clip, vae);
    }

    private (WGNodeData model, WGNodeData clip) ApplyConfinement(int sectionId, WGNodeData model, WGNodeData clip)
    {
        (JArray mArray, JArray cArray) = g.LoadLorasForConfinement(sectionId, model.Path, clip.Path);
        return (
            new WGNodeData(mArray, g, WGNodeData.DT_MODEL, g.CurrentCompat()),
            new WGNodeData(cArray, g, WGNodeData.DT_TEXTENC, g.CurrentCompat())
        );
    }

    private (WGNodeData vae, bool mustReencode) ResolveVae(
        StageSpec stage,
        bool isFinalStep,
        WGNodeData vae,
        bool mustReencode)
    {
        if (stage.Vae is not null)
        {
            JArray vaeArray = g.CreateVAELoader(stage.Vae.ToString(g.ModelFolderFormat));
            vae = new WGNodeData(vaeArray, g, WGNodeData.DT_VAE, g.CurrentCompat());
            g.CurrentVae = vae;
            return (vae, true);
        }

        if (!isFinalStep
            && stage.ModelSource == ModelSource.Refiner
            && g.UserInput.TryGet(T2IParamTypes.RefinerVAE, out T2IModel refinerVaeOverride)
            && refinerVaeOverride is not null)
        {
            JArray vaeArray = g.CreateVAELoader(refinerVaeOverride.ToString(g.ModelFolderFormat));
            vae = new WGNodeData(vaeArray, g, WGNodeData.DT_VAE, g.CurrentCompat());
            g.CurrentVae = vae;
            return (vae, true);
        }

        return (vae, mustReencode);
    }

    private (WGNodeData model, WGNodeData clip) ApplyLoraStack(
        StageSpec stage,
        int stageSectionId,
        WGNodeData model,
        WGNodeData clip)
    {
        if (stage.Loras.IsEmpty)
        {
            return (model, clip);
        }

        using (ParamSnapshot snapshot = ModelPrep.SnapshotLoraParams(g))
        {
            snapshot.Remove();
            List<string> confinements = [.. Enumerable.Repeat($"{stageSectionId}", stage.Loras.Names.Count)];
            g.UserInput.Set(T2IParamTypes.Loras, [.. stage.Loras.Names]);
            g.UserInput.Set(T2IParamTypes.LoraWeights, [.. stage.Loras.Weights]);
            g.UserInput.Set(T2IParamTypes.LoraTencWeights, [.. stage.Loras.TencWeights]);
            g.UserInput.Set(T2IParamTypes.LoraSectionConfinement, confinements);
            (JArray mArray, JArray cArray) = g.LoadLorasForConfinement(stageSectionId, model.Path, clip.Path);
            model = new WGNodeData(mArray, g, WGNodeData.DT_MODEL, g.CurrentCompat());
            clip = new WGNodeData(cArray, g, WGNodeData.DT_TEXTENC, g.CurrentCompat());
        }

        return (model, clip);
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
            g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(reusedImage), g, WGNodeData.DT_IMAGE, g.CurrentCompat());
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

        WGNodeData currentImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);
        if (shouldSave && currentImageOut is not null)
        {
            string preEditSaveNodeId = g.GetStableDynamicID(PreEditImageSaveId, stageIndex);
            if (!g.Workflow.ContainsKey(preEditSaveNodeId))
            {
                new WGNodeData(currentImageOut.Path, g, WGNodeData.DT_IMAGE, g.CurrentCompat()).SaveOutput(null, null, id: preEditSaveNodeId);
            }
            Logs.Debug("Base2Edit: Saved pre-edit image");
        }
    }

    private void ReencodeIfNeeded(EditStageContext ctx, ReencodeOptions options = default)
    {
        ModelState modelState = ctx.ModelState;
        WGNodeData currentImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);
        if (options.ForceFromCurrentImage && currentImageOut is not null)
        {
            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, modelState.Vae.Path, out INodeOutput imageTailSamples))
            {
                g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(imageTailSamples), g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat())
                {
                    Width = currentImageOut.Width,
                    Height = currentImageOut.Height
                };
            }
            else
            {
                g.CurrentMedia = currentImageOut.EncodeToLatent(modelState.Vae);
            }
            return;
        }

        if (currentImageOut is not null &&
            VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, modelState.Vae.Path, out INodeOutput reusedSamples))
        {
            g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(reusedSamples), g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat())
            {
                Width = currentImageOut.Width,
                Height = currentImageOut.Height
            };
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

        g.CurrentMedia = currentImageOut.EncodeToLatent(modelState.Vae);
    }

    private JArray EnsureCurrentSamplesForEdit(WGNodeData preferredVae)
    {
        return TryEnsureCurrentSamplesForEdit(preferredVae)
            ?? throw new SwarmReadableErrorException("Base2Edit: No latent anchor is available for edit-stage sampling.");
    }

    private JArray TryEnsureCurrentSamplesForEdit(WGNodeData preferredVae)
    {
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (currentSamples is not null)
        {
            return currentSamples.Path;
        }

        WGNodeData currentImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);
        JArray vae = (preferredVae ?? g.CurrentVae).Path;
        if (currentImageOut is null || vae is null)
        {
            return null;
        }

        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, currentImageOut.Path, vae, out INodeOutput reusedSamples))
        {
            return WorkflowBridge.ToPath(reusedSamples);
        }

        return currentImageOut.EncodeToLatent(new WGNodeData(vae, g, WGNodeData.DT_VAE, g.CurrentCompat())).Path;
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
            ? TryEnsureCurrentSamplesForEdit(currentStageVae)
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

            g.CurrentMedia = new WGNodeData([samplerNode, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat())
            {
                Width = editParams.Width,
                Height = editParams.Height
            };
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

        WGNodeData currentImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (allowFinalDecodeRetarget &&
            currentImageOut is not null &&
            currentSamples is not null &&
            VaeNodeReuse.TryRetargetUnconsumedVaeDecode(g, currentImageOut.Path, vae.Path, currentSamples.Path, out INodeOutput retargetedImage))
        {
            g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(retargetedImage), g, WGNodeData.DT_IMAGE, g.CurrentCompat());
            return;
        }

        if (currentSamples is not null && VaeNodeReuse.ReuseVaeDecodeForSamplesAndVae(g, currentSamples.Path, vae.Path, out INodeOutput reusedImage))
        {
            g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(reusedImage), g, WGNodeData.DT_IMAGE, g.CurrentCompat());
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
        string upscaleMethod = ctx.Stage.UpscaleMethod;
        if (upscale == 1)
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
            WGNodeData decoded = g.CurrentMedia?.AsRawImage(g.CurrentVae);
            if (decoded is null)
            {
                return (baseWidth, baseHeight);
            }

            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            JArray upscaledImageRef;
            if (upscaleMethod.StartsWith("pixel-", StringComparison.OrdinalIgnoreCase))
            {
                string pixelMethod = upscaleMethod["pixel-".Length..];
                ImageScaleNode scaledPixel = AddImageScale(bridge, decoded.Path, width, height, pixelMethod, "disabled");
                upscaledImageRef = WorkflowBridge.ToPath(scaledPixel.IMAGE);
            }
            else
            {
                string modelName = upscaleMethod["model-".Length..];
                UpscaleModelLoaderNode loader = AddUpscaleModelLoader(bridge, modelName);
                ImageUpscaleWithModelNode modelUpscale = AddImageUpscaleWithModel(bridge, WorkflowBridge.ToPath(loader.UPSCALEMODEL), decoded.Path);
                ImageScaleNode scaledModel = AddImageScale(bridge, WorkflowBridge.ToPath(modelUpscale.IMAGE), width, height, "lanczos", "disabled");
                upscaledImageRef = WorkflowBridge.ToPath(scaledModel.IMAGE);
            }

            g.CurrentMedia = new WGNodeData(upscaledImageRef, g, WGNodeData.DT_IMAGE, g.CurrentCompat())
            {
                Width = width,
                Height = height
            };

            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, upscaledImageRef, stageVae.Path, out INodeOutput reusedSamples))
            {
                g.CurrentMedia = new WGNodeData(WorkflowBridge.ToPath(reusedSamples), g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
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
                WGNodeData imageMedia = g.CurrentMedia?.AsRawImage(g.CurrentVae);
                if (imageMedia is null || stageVae is null)
                {
                    return (baseWidth, baseHeight);
                }

                if (VaeNodeReuse.ReuseVaeEncodeForImage(g, imageMedia.Path, stageVae.Path, out INodeOutput reusedLatent))
                {
                    latentMedia = new WGNodeData(WorkflowBridge.ToPath(reusedLatent), g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                }
                else
                {
                    latentMedia = imageMedia.EncodeToLatent(stageVae);
                }
            }

            g.CurrentMedia = new WGNodeData(latentMedia.Path, g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
            string latentMethod = upscaleMethod["latent-".Length..];
            WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            LatentUpscaleByNode latentUpscale = AddLatentUpscaleBy(bridge, g.CurrentMedia.Path, latentMethod, upscale);
            g.CurrentMedia = g.CurrentMedia.WithPath(WorkflowBridge.ToPath(latentUpscale.LATENT));
            g.CurrentMedia.Width = width;
            g.CurrentMedia.Height = height;
            return (width, height);
        }

        return (baseWidth, baseHeight);
    }

    private ImageScaleNode AddImageScale(WorkflowBridge bridge, JArray imagePath, int width, int height, string method, string crop)
    {
        ImageScaleNode node = bridge.AddNode(
            new ImageScaleNode().With(
                Width: width,
                Height: height,
                UpscaleMethod: method,
                Crop: crop),
            id: $"{g.LastID++}");
        node.Image.ConnectFromPath(bridge, imagePath);
        return node;
    }

    private UpscaleModelLoaderNode AddUpscaleModelLoader(WorkflowBridge bridge, string modelName)
    {
        UpscaleModelLoaderNode node = bridge.AddNode(
            new UpscaleModelLoaderNode().With(ModelName: modelName),
            id: $"{g.LastID++}");
        return node;
    }

    private ImageUpscaleWithModelNode AddImageUpscaleWithModel(WorkflowBridge bridge, JArray modelPath, JArray imagePath)
    {
        ImageUpscaleWithModelNode node = bridge.AddNode(
            new ImageUpscaleWithModelNode(),
            id: $"{g.LastID++}");
        node.UpscaleModel.ConnectFromPath(bridge, modelPath);
        node.Image.ConnectFromPath(bridge, imagePath);
        return node;
    }

    private LatentUpscaleByNode AddLatentUpscaleBy(WorkflowBridge bridge, JArray samplesPath, string method, double scaleBy)
    {
        LatentUpscaleByNode node = bridge.AddNode(
            new LatentUpscaleByNode().With(
                UpscaleMethod: method,
                ScaleBy: scaleBy),
            id: $"{g.LastID++}");
        node.Samples.ConnectFromPath(bridge, samplesPath);
        return node;
    }
}
