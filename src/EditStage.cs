using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

internal record ModelState(
    WGNodeData Model,
    WGNodeData Clip,
    WGNodeData Vae,
    WGNodeData PreEditVae,
    bool MustReencode
);

internal record Parameters(
    int Width,
    int Height,
    int Steps,
    double CfgScale,
    double Control,
    bool RefineOnly,
    double Guidance,
    long Seed,
    string Sampler,
    string Scheduler
);

internal record Conditioning(JArray Positive, JArray Negative);

internal readonly record struct RunEditStageOptions(
    bool TrackResolvedModelForMetadata = true,
    bool AllowFinalDecodeRetarget = true,
    bool ForceReencodeFromCurrentImage = false,
    bool RewireFinalConsumers = true
);

internal readonly record struct ReencodeOptions(
    bool ForceFromCurrentImage = false
);

public class EditStage
{
    private const int BranchEditSaveId = 50300;

    public readonly WorkflowGenerator g;
    public readonly StageRefStore store;
    private readonly StageRunner runner;
    private Dictionary<int, StageSpec> _parsedStages;

    public EditStage(WorkflowGenerator g, StageRefStore store)
    {
        this.g = g;
        this.store = store;
        this.runner = new StageRunner(g, store);
    }

    private WGNodeData WrapLatent(JArray path) => new(path, g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
    private WGNodeData WrapImage(JArray path) => new(path, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
    private WGNodeData WrapVae(JArray path) => new(path, g, WGNodeData.DT_VAE, g.CurrentCompat());

    public void Run(bool isFinalStep)
    {
        if (!isFinalStep)
        {
            store.Capture(StageRefStore.StageKind.Base);
        }

        bool preferCurrentImageAnchor = ShouldPreferCurrentImageAnchor(isFinalStep);
        if (isFinalStep)
        {
            NormalizeFinalStepAnchor(preferCurrentImageAnchor);
            store.Capture(StageRefStore.StageKind.Refiner);
        }

        (List<StageSpec> primaryChain, List<StageSpec> branches) =
            GetPrimaryChainAndBranches(isFinalStep);

        foreach (StageSpec stage in primaryChain)
        {
            RestoreParentPipelineState(stage.ApplyAfter);
            ApplyStageOverrides(stage);
            runner.RunStage(
                isFinalStep: isFinalStep,
                stage: stage,
                options: new RunEditStageOptions(
                    TrackResolvedModelForMetadata: true,
                    AllowFinalDecodeRetarget: branches.Count == 0,
                    ForceReencodeFromCurrentImage: preferCurrentImageAnchor,
                    RewireFinalConsumers: !preferCurrentImageAnchor
                )
            );
            store.Capture(StageRefStore.StageKind.Edit, stage.Id);
        }

        if (branches.Count > 0)
        {
            WGNodeData primarySamples = WGNodeDataUtil.TryGetCurrentLatent(g);
            WGNodeData primaryVae = g.CurrentVae;
            WGNodeData primaryImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);

            foreach (StageSpec branch in branches)
            {
                RestoreParentPipelineState(branch.ApplyAfter);
                ApplyStageOverrides(branch);
                runner.RunStage(
                    isFinalStep: isFinalStep,
                    stage: branch,
                    options: new RunEditStageOptions(
                        TrackResolvedModelForMetadata: false,
                        AllowFinalDecodeRetarget: false,
                        ForceReencodeFromCurrentImage: preferCurrentImageAnchor,
                        RewireFinalConsumers: !preferCurrentImageAnchor
                    )
                );
                store.Capture(StageRefStore.StageKind.Edit, branch.Id);
                SaveBranchOutput(branch.Id);

                if (primarySamples is not null)
                {
                    g.CurrentMedia = WrapLatent(primarySamples.Path);
                }
                if (primaryVae is not null)
                {
                    g.CurrentVae = primaryVae;
                }
                if (primaryImageOut is not null)
                {
                    g.CurrentMedia = WrapImage(primaryImageOut.Path);
                }
            }
        }

        if (isFinalStep)
        {
            runner.CleanupDanglingVaeDecodeNodes();
        }
    }

    private void SaveBranchOutput(int branchId)
    {
        WGNodeData branchImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);

        if (branchImageOut is null)
        {
            WGNodeData branchSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
            WGNodeData branchVae = g.CurrentVae;
            if (branchSamples is null || branchVae is null)
            {
                return;
            }
            branchImageOut = WrapLatent(branchSamples.Path).DecodeLatents(branchVae, false);
        }

        if (!VaeNodeReuse.HasSaveForImage(g, branchImageOut.Path))
        {
            WrapImage(branchImageOut.Path)
                .SaveOutput(null, null, id: g.GetStableDynamicID(BranchEditSaveId, branchId));
        }
    }

    private (List<StageSpec> PrimaryChain, List<StageSpec> Branches) GetPrimaryChainAndBranches(bool isFinalStep)
    {
        IReadOnlyList<StageSpec> stages = GetCachedParsedStages();
        if (stages.Count == 0)
        {
            return ([], []);
        }

        List<StageSpec> roots = [.. stages
            .Where(stage => StringUtils.Equals(stage.ApplyAfter, isFinalStep ? "Refiner" : "Base"))
            .OrderBy(stage => stage.Id)];

        if (roots.Count == 0)
        {
            return ([], []);
        }

        List<StageSpec> primaryChain = [];
        List<StageSpec> branches = [];
        CollectStageChain(roots[0], primaryChain, branches);
        branches.AddRange(roots.Skip(1));

        HashSet<int> seen = [];
        branches = [.. branches
            .OrderBy(stage => stage.Id)
            .Where(stage => seen.Add(stage.Id))];

        return (primaryChain, branches);
    }

    private IReadOnlyList<StageSpec> GetCachedParsedStages()
    {
        if (_parsedStages is null || _parsedStages.Count == 0)
        {
            _parsedStages = [];
            foreach (StageSpec stage in Base2EditSpecParser.Parse(g))
            {
                _parsedStages[stage.Id] = stage;
            }
        }

        return [.. _parsedStages.Values.OrderBy(stage => stage.Id)];
    }

    private static void CollectStageChain(
        StageSpec stage,
        List<StageSpec> result,
        List<StageSpec> branches)
    {
        result.Add(stage);

        if (stage.Children is null || stage.Children.Count == 0)
        {
            return;
        }

        List<StageSpec> orderedChildren = [.. stage.Children.OrderBy(c => c.Id)];
        StageSpec child = orderedChildren[0];
        CollectStageChain(child, result, branches);

        foreach (StageSpec siblingBranch in orderedChildren.Skip(1))
        {
            branches.Add(siblingBranch);
        }
    }

    private void NormalizeFinalStepAnchor(bool preferCurrentImageAnchor = false)
    {
        if (preferCurrentImageAnchor)
        {
            return;
        }

        WGNodeData currentImageOut = g.CurrentMedia?.AsRawImage(g.CurrentVae);
        if (currentImageOut is null)
        {
            return;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        (int resolvedWidth, int resolvedHeight) = ResolveImageDimensionsForAnchor(bridge, currentImageOut);
        int anchorWidth = Math.Max(resolvedWidth, 16);
        int anchorHeight = Math.Max(resolvedHeight, 16);

        if (TryResolveNearestAnchor(
                bridge,
                samplesRef: null,
                imageRef: currentImageOut.Path,
                out JArray imagePathSamples,
                out JArray imagePathImageOut,
                out JArray imagePathVae))
        {
            ApplyAnchorState(imagePathSamples, imagePathImageOut, imagePathVae);
            ApplyAnchorDimensions(anchorWidth, anchorHeight);
            return;
        }

        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (currentSamples is not null
            && TryResolveNearestAnchor(
                bridge,
                samplesRef: currentSamples.Path,
                imageRef: currentImageOut.Path,
                out JArray anchorSamples,
                out JArray anchorImageOut,
                out JArray anchorVae))
        {
            ApplyAnchorState(anchorSamples, anchorImageOut, anchorVae);
            ApplyAnchorDimensions(anchorWidth, anchorHeight);
        }
    }

    private static bool TryResolveNearestAnchor(
        WorkflowBridge bridge,
        JArray samplesRef,
        JArray imageRef,
        out JArray anchorSamples,
        out JArray anchorImageOut,
        out JArray anchorVae)
    {
        anchorSamples = null;
        anchorImageOut = null;
        anchorVae = null;

        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];

        void enqueueProducer(JArray nodeRef)
        {
            if (nodeRef is null || nodeRef.Count != 2)
            {
                return;
            }
            if (bridge.ResolvePath(nodeRef) is INodeOutput produced
                && visited.Add(produced.Node.Id))
            {
                pending.Enqueue(produced.Node);
            }
        }

        enqueueProducer(samplesRef);
        enqueueProducer(imageRef);

        while (pending.Count > 0)
        {
            ComfyNode node = pending.Dequeue();

            if (node is SwarmKSamplerNode or KSamplerAdvancedNode)
            {
                anchorSamples = new JArray(node.Id, 0);
                return true;
            }

            if (node is VAEDecodeNode or VAEDecodeTiledNode)
            {
                anchorImageOut = new JArray(node.Id, 0);
                INodeOutput samplesConn = node.FindInput("samples")?.Connection
                                          ?? node.FindInput("latent")?.Connection;
                if (samplesConn is not null)
                {
                    anchorSamples = new JArray(samplesConn.Node.Id, samplesConn.SlotIndex);
                }
                if (node.FindInput("vae")?.Connection is INodeOutput vaeConn)
                {
                    anchorVae = new JArray(vaeConn.Node.Id, vaeConn.SlotIndex);
                }
                return anchorSamples is not null || anchorImageOut is not null;
            }

            foreach (INodeInput input in node.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream && visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }

        return false;
    }

    private void RestoreParentPipelineState(string applyAfter)
    {
        StageRefStore.StageRef parentRef = null;

        if (StringUtils.Equals(applyAfter, "Base"))
        {
            parentRef = store.Base;
        }
        else if (StringUtils.Equals(applyAfter, "Refiner"))
        {
            parentRef = store.Refiner;
        }
        else if (StageRefStore.TryParseStageIndexKey(applyAfter, out int parentId))
        {
            store.TryGetEditRef(parentId, out parentRef);
        }

        if (parentRef is null)
        {
            return;
        }

        if (parentRef.Media is not null)
        {
            g.CurrentMedia = parentRef.Media;
        }
        if (parentRef.Vae is not null)
        {
            g.CurrentVae = parentRef.Vae;
        }
    }

    private void ApplyAnchorState(JArray samples, JArray imageOut, JArray vae)
    {
        if (samples is not null)
        {
            g.CurrentMedia = WrapLatent(samples);
        }
        if (imageOut is not null)
        {
            g.CurrentMedia = WrapImage(imageOut);
        }
        if (vae is not null)
        {
            g.CurrentVae = WrapVae(vae);
        }
    }

    private void ApplyAnchorDimensions(int width, int height)
    {
        if (g.CurrentMedia is null)
        {
            return;
        }

        g.CurrentMedia.Width ??= width;
        g.CurrentMedia.Height ??= height;
    }

    private (int Width, int Height) ResolveImageDimensionsForAnchor(WorkflowBridge bridge, WGNodeData imageOut)
    {
        int fallbackWidth = g.UserInput.GetImageWidth();
        int fallbackHeight = g.UserInput.GetImageHeight();
        if (imageOut is null)
        {
            return (fallbackWidth, fallbackHeight);
        }

        if ((imageOut.Width ?? 0) > 0 && (imageOut.Height ?? 0) > 0)
        {
            return (imageOut.Width!.Value, imageOut.Height!.Value);
        }

        if (imageOut.Path?.Count == 2
            && bridge.Graph.GetNode($"{imageOut.Path[0]}") is ComfyNode node)
        {
            int? widthFromNode = node.FindInput("width").LiteralAsInt();
            int? heightFromNode = node.FindInput("height").LiteralAsInt();
            if (widthFromNode > 0 && heightFromNode > 0)
            {
                return (widthFromNode.Value, heightFromNode.Value);
            }
        }

        return (fallbackWidth, fallbackHeight);
    }

    private bool ShouldPreferCurrentImageAnchor(bool isFinalStep)
    {
        if (!isFinalStep)
        {
            return false;
        }

        string segmentApplyAfter = g.UserInput.Get(T2IParamTypes.SegmentApplyAfter, "Refiner");
        if (!StringUtils.Equals(segmentApplyAfter, "Refiner"))
        {
            return false;
        }

        PromptRegion prompt = new(g.UserInput.Get(T2IParamTypes.Prompt, ""));
        return prompt.Parts.Any(p => p.Type == PromptRegion.PartType.Segment);
    }

    private void ApplyStageOverrides(StageSpec stage)
    {
        int sectionId = Base2EditExtension.EditSectionIdForStage(stage.Id);
        if (stage.HasVaeOverride)
        {
            g.UserInput.Set(Base2EditExtension.EditVAE.Type, stage.Vae, sectionId);
        }
        else
        {
            g.UserInput.Remove(Base2EditExtension.EditVAE, sectionId);
        }
    }
}
