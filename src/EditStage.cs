using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.Builtin_ComfyUIBackend;
using Newtonsoft.Json.Linq;

namespace Base2Edit;

internal record ModelState(
    JArray Model,
    JArray Clip,
    JArray Vae,
    JArray PreEditVae,
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

    /// <summary>
    /// Main entry point for running edit stages. The first root stage is the "primary" — it
    /// (and any children chained via ApplyAfter) continues the main pipeline to the final save
    /// node. All other root stages with the same ApplyAfter are "branches" that fork from the
    /// same anchor, save their output independently, and do not affect the primary pipeline.
    /// </summary>
    public void Run(bool isFinalStep)
    {
        ParamSnapshot snapshot = SnapshotStageParams(g);
        try
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
                WGNodeData primaryImageOut = WGNodeDataUtil.TryGetCurrentImage(g);

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
        finally
        {
            snapshot.Restore();
        }
    }

    private void SaveBranchOutput(int branchId)
    {
        WGNodeData branchImageOut = WGNodeDataUtil.TryGetCurrentImage(g);

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

    /// <summary>
    /// Splits root stages into the primary chain and branch stages. The first root (by ID) is
    /// the primary — it and its children form a chain that continues the main pipeline. All
    /// remaining roots are branches that dead-end into their own save node.
    /// </summary>
    private (List<StageSpec> PrimaryChain, List<StageSpec> Branches) GetPrimaryChainAndBranches(bool isFinalStep)
    {
        IReadOnlyList<StageSpec> stages = GetCachedParsedStages();
        if (stages.Count == 0)
        {
            return ([], []);
        }

        List<StageSpec> roots = [.. stages
            .Where(stage => string.Equals(stage.ApplyAfter, isFinalStep ? "Refiner" : "Base", StringComparison.OrdinalIgnoreCase))
            .OrderBy(stage => stage.Id)];

        if (roots.Count == 0)
        {
            return ([], []);
        }

        List<StageSpec> primaryChain = [];
        List<StageSpec> branches = [];
        CollectStageChain(roots[0], primaryChain, branches);
        branches.AddRange(roots.Skip(1));

        // Keep deterministic execution order and avoid duplicates when branch subtrees overlap.
        HashSet<int> seen = [];
        branches = [.. branches
            .OrderBy(stage => stage.Id)
            .Where(stage => seen.Add(stage.Id))];

        return (primaryChain, branches);
    }

    /// <summary>
    /// Lazily parses the edit stage JSON and caches the result for this run.
    /// Returns all parsed stages ordered by ID.
    /// </summary>
    private IReadOnlyList<StageSpec> GetCachedParsedStages()
    {
        if (_parsedStages is null || _parsedStages.Count == 0)
        {
            _parsedStages = [];
            foreach (StageSpec stage in Base2EditSpecParser.ParseEditStages(g))
            {
                _parsedStages[stage.Id] = stage;
            }
        }

        return [.. _parsedStages.Values.OrderBy(stage => stage.Id)];
    }

    /// <summary>
    /// Adds a stage and then its first child (depth-first) to the result list.
    /// Each stage can have at most one child.
    /// </summary>
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
            // Branch stages are intentionally single-stage leaves.
            // Any child stages attached to a branch are ignored.
            branches.Add(siblingBranch);
        }
    }

    /// <summary>
    /// Resolves the pipeline anchor for the final (refiner) step by tracing back from the
    /// current image output to find the nearest sampler or VAE decode node. This ensures the
    /// edit stage attaches to the correct point in the workflow rather than a stale reference.
    /// Skipped when the current image anchor is preferred (e.g. segment-after-refiner workflows).
    /// </summary>
    private void NormalizeFinalStepAnchor(bool preferCurrentImageAnchor = false)
    {
        if (preferCurrentImageAnchor)
        {
            return;
        }

        WGNodeData currentImageOut = WGNodeDataUtil.TryGetCurrentImage(g);
        if (currentImageOut is null)
        {
            return;
        }
        (int resolvedWidth, int resolvedHeight) = ResolveImageDimensionsForAnchor(currentImageOut);
        int anchorWidth = Math.Max(resolvedWidth, 16);
        int anchorHeight = Math.Max(resolvedHeight, 16);

        // Prefer resolving from the current image tail first. This avoids anchoring to a stale
        // sampler when latent media still points at refiner output but current image media has drifted
        // into a downstream chain.
        if (WorkflowUtils.TryResolveNearestSamplerOrDecodeAnchor(
                g.Workflow,
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

        // Fallback: also seed the search with the current latent if available, so the walk
        // can reach a sampler that doesn't have a direct image-path connection.
        WGNodeData currentSamples = WGNodeDataUtil.TryGetCurrentLatent(g);
        if (currentSamples is not null
            && WorkflowUtils.TryResolveNearestSamplerOrDecodeAnchor(
                g.Workflow,
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

    /// <summary>
    /// Restores the pipeline (media + VAE) to the captured state of a stage's parent.
    /// Ensures each stage receives the correct input based on its ApplyAfter target.
    /// For root stages (ApplyAfter = Base/Refiner), this is effectively a no-op since the
    /// pipeline is already in the correct state.
    /// </summary>
    private void RestoreParentPipelineState(string applyAfter)
    {
        StageRefStore.StageRef parentRef = null;

        if (string.Equals(applyAfter, "Base", StringComparison.OrdinalIgnoreCase))
        {
            parentRef = store.Base;
        }
        else if (string.Equals(applyAfter, "Refiner", StringComparison.OrdinalIgnoreCase))
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

    private (int Width, int Height) ResolveImageDimensionsForAnchor(WGNodeData imageOut)
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
            && g.Workflow.TryGetValue($"{imageOut.Path[0]}", out JToken nodeTok)
            && nodeTok is JObject node
            && node["inputs"] is JObject inputs)
        {
            if (inputs.TryGetValue("width", out JToken widthTok)
                && inputs.TryGetValue("height", out JToken heightTok)
                && int.TryParse($"{widthTok}", out int widthFromNode)
                && int.TryParse($"{heightTok}", out int heightFromNode)
                && widthFromNode > 0
                && heightFromNode > 0)
            {
                return (widthFromNode, heightFromNode);
            }
        }

        return (fallbackWidth, fallbackHeight);
    }

    /// <summary>
    /// Policy gate for final-step anchor behavior. Segment-after-refiner workflows should anchor
    /// from the current image tail so Base2Edit runs after those segment stages.
    /// </summary>
    private bool ShouldPreferCurrentImageAnchor(bool isFinalStep)
    {
        if (!isFinalStep)
        {
            return false;
        }

        string segmentApplyAfter = g.UserInput.Get(T2IParamTypes.SegmentApplyAfter, "Refiner");
        if (!string.Equals(segmentApplyAfter, "Refiner"))
        {
            return false;
        }

        PromptRegion prompt = new(g.UserInput.Get(T2IParamTypes.Prompt, ""));
        return prompt.Parts.Any(p => p.Type == PromptRegion.PartType.Segment);
    }

    private static ParamSnapshot SnapshotStageParams(WorkflowGenerator g) =>
        ParamSnapshot.Of(g.UserInput, Base2EditExtension.EditVAE.Type);

    /// <summary>
    /// Writes the stage's VAE override (or removes it) into UserInput so downstream node builders
    /// pick it up. All other stage params (steps, CFG, sampler, etc.) are now read directly off
    /// the StageSpec carried in EditStageContext — no UserInput round-trip needed.
    /// </summary>
    private void ApplyStageOverrides(StageSpec stage)
    {
        if (stage.HasVaeOverride)
        {
            g.UserInput.Set(Base2EditExtension.EditVAE.Type, stage.Vae);
        }
        else
        {
            g.UserInput.Remove(Base2EditExtension.EditVAE);
        }
    }
}
