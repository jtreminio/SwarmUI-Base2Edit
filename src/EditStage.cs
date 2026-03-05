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
    private Dictionary<int, JsonParser.StageSpec> _parsedStages;

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

            (List<JsonParser.StageSpec> primaryChain, List<JsonParser.StageSpec> branches) =
                GetPrimaryChainAndBranches(isFinalStep);

            foreach (JsonParser.StageSpec stage in primaryChain)
            {
                RestoreParentPipelineState(stage.ApplyAfter);
                ApplyStageOverrides(stage);
                runner.RunStage(
                    isFinalStep: isFinalStep,
                    stageIndex: stage.Id,
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

                foreach (JsonParser.StageSpec branch in branches)
                {
                    RestoreParentPipelineState(branch.ApplyAfter);
                    ApplyStageOverrides(branch);
                    runner.RunStage(
                        isFinalStep: isFinalStep,
                        stageIndex: branch.Id,
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
    private (List<JsonParser.StageSpec> PrimaryChain, List<JsonParser.StageSpec> Branches) GetPrimaryChainAndBranches(bool isFinalStep)
    {
        IReadOnlyList<JsonParser.StageSpec> stages = GetCachedParsedStages();
        if (stages.Count == 0)
        {
            return ([], []);
        }

        List<JsonParser.StageSpec> roots = [.. stages
            .Where(stage => string.Equals(stage.ApplyAfter, isFinalStep ? "Refiner" : "Base", StringComparison.OrdinalIgnoreCase))
            .OrderBy(stage => stage.Id)];

        if (roots.Count == 0)
        {
            return ([], []);
        }

        List<JsonParser.StageSpec> primaryChain = [];
        CollectStageChain(roots[0], primaryChain);

        List<JsonParser.StageSpec> branches = roots.Count > 1 ? roots[1..] : [];

        return (primaryChain, branches);
    }

    /// <summary>
    /// Lazily parses the edit stage JSON and caches the result for this run.
    /// Returns all parsed stages ordered by ID.
    /// </summary>
    private IReadOnlyList<JsonParser.StageSpec> GetCachedParsedStages()
    {
        if (_parsedStages is null || _parsedStages.Count == 0)
        {
            _parsedStages = [];
            foreach (JsonParser.StageSpec stage in new JsonParser(g).ParseEditStages())
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
    private static void CollectStageChain(JsonParser.StageSpec stage, List<JsonParser.StageSpec> result)
    {
        result.Add(stage);

        if (stage.Children is null || stage.Children.Count == 0)
        {
            return;
        }

        JsonParser.StageSpec child = stage.Children.OrderBy(c => c.Id).First();
        CollectStageChain(child, result);
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
        ParamSnapshot.Of(g.UserInput,
            Base2EditExtension.KeepPreEditImage.Type,
            Base2EditExtension.EditRefineOnly.Type,
            Base2EditExtension.ApplyEditAfter.Type,
            Base2EditExtension.EditControl.Type,
            Base2EditExtension.EditModel.Type,
            Base2EditExtension.EditVAE.Type,
            Base2EditExtension.EditUpscale.Type,
            Base2EditExtension.EditUpscaleMethod.Type,
            Base2EditExtension.EditSteps.Type,
            Base2EditExtension.EditCFGScale.Type,
            Base2EditExtension.EditSampler.Type,
            Base2EditExtension.EditScheduler.Type
        );

    /// <summary>
    /// Writes the stage's per-stage parameter overrides (model, VAE, steps, CFG scale, sampler, etc.)
    /// into the generator's UserInput so downstream node builders pick them up. Optional
    /// overrides (VAE, CFG scale, sampler, scheduler) are removed from UserInput when not specified
    /// by the stage, allowing fallback to global defaults.
    /// </summary>
    private void ApplyStageOverrides(JsonParser.StageSpec stage)
    {
        g.UserInput.Set(Base2EditExtension.KeepPreEditImage.Type, stage.KeepPreEditImage ? "true" : "false");
        g.UserInput.Set(Base2EditExtension.EditRefineOnly.Type, stage.RefineOnly ? "true" : "false");
        g.UserInput.Set(Base2EditExtension.ApplyEditAfter.Type, stage.ApplyAfter);
        g.UserInput.Set(Base2EditExtension.EditControl.Type, $"{stage.Control}");
        g.UserInput.Set(Base2EditExtension.EditModel.Type, stage.Model);
        g.UserInput.Set(Base2EditExtension.EditUpscale.Type, $"{stage.Upscale}");
        g.UserInput.Set(Base2EditExtension.EditUpscaleMethod.Type, stage.UpscaleMethod);
        g.UserInput.Set(Base2EditExtension.EditSteps.Type, $"{stage.Steps}");

        if (stage.HasVaeOverride)
        {
            g.UserInput.Set(Base2EditExtension.EditVAE.Type, stage.Vae);
        }
        else
        {
            g.UserInput.Remove(Base2EditExtension.EditVAE);
        }

        g.UserInput.Set(Base2EditExtension.EditCFGScale.Type, $"{stage.CfgScale}");
        g.UserInput.Set(Base2EditExtension.EditSampler.Type, stage.Sampler);
        g.UserInput.Set(Base2EditExtension.EditScheduler.Type, stage.Scheduler);
    }
}
