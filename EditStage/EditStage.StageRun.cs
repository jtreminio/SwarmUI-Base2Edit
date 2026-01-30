using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public partial class EditStage
{
    private static void RunStages(WorkflowGenerator g, List<StageSpec> stages, bool isFinalStep)
    {
        using StageParamSnapshot snapshot = new(g);
        try
        {
            List<ResolvedStage> resolved = ResolveStages(stages);
            StageHook currentHook = isFinalStep ? StageHook.Refiner : StageHook.Base;
            HashSet<int> executed = [];

            // Group stages by (Hook, DependsOnStageId). Stages in the same group share the same anchor
            // (ex both "after refiner"). Primary = lowest Id in group (continues pipeline); rest run in parallel (save and stop)
            List<List<ResolvedStage>> groups = resolved
                .Where(st => st.Hook == currentHook)
                .GroupBy(st => (st.Hook, st.DependsOnStageId))
                .OrderBy(grp => grp.Min(s => s.Spec.Id))
                .Select(grp => grp.OrderBy(s => s.Spec.Id).ToList())
                .ToList();

            foreach (List<ResolvedStage> group in groups)
            {
                if (group.Count == 0)
                {
                    continue;
                }

                if (group[0].DependsOnStageId.HasValue && !executed.Contains(group[0].DependsOnStageId.Value))
                {
                    int depId = group[0].DependsOnStageId.Value;
                    throw new SwarmReadableErrorException(
                        $"Base2Edit: Edit Stage {group[0].Spec.Id} depends on Edit Stage {depId} which is not executed in this phase."
                    );
                }

                // Anchor = pipeline state before this group (same input for all stages in the group)
                JArray anchorSamples = g.FinalSamples;
                JArray anchorVae = g.FinalVae;
                JArray anchorImageOut = g.FinalImageOut;

                ResolvedStage primary = group[0];
                ApplyStageOverrides(g, primary.Spec);
                RunEditStage(g, isFinalStep: isFinalStep, stageIndex: primary.Spec.Id);
                executed.Add(primary.Spec.Id);

                JArray primarySamples = g.FinalSamples;
                JArray primaryVae = g.FinalVae;
                JArray primaryImageOut = g.FinalImageOut;

                // Parallel branches: same anchor as primary, run edit, save image, then restore pipeline to primary output
                for (int i = 1; i < group.Count; i++)
                {
                    ResolvedStage parallel = group[i];
                    g.FinalSamples = anchorSamples;
                    g.FinalVae = anchorVae;
                    g.FinalImageOut = anchorImageOut;

                    ApplyStageOverrides(g, parallel.Spec);
                    RunEditStage(g, isFinalStep: isFinalStep, stageIndex: parallel.Spec.Id);

                    JArray parallelSamples = g.FinalSamples;
                    JArray parallelVae = g.FinalVae;
                    JArray parallelImageOut = g.FinalImageOut;

                    if (isFinalStep && parallelImageOut is not null)
                    {
                        if (!VaeNodeReuse.HasSaveForImage(g, parallelImageOut))
                        {
                            g.CreateImageSaveNode(parallelImageOut, g.GetStableDynamicID(ParallelEditSaveId, parallel.Spec.Id));
                        }
                    }
                    else if (!isFinalStep && parallelSamples is not null && parallelVae is not null)
                    {
                        string decodeNode = g.CreateVAEDecode(parallelVae, parallelSamples);
                        JArray decodedRef = [decodeNode, 0];
                        if (!VaeNodeReuse.HasSaveForImage(g, decodedRef))
                        {
                            g.CreateImageSaveNode(decodedRef, g.GetStableDynamicID(ParallelEditSaveId, parallel.Spec.Id));
                        }
                    }

                    g.FinalSamples = primarySamples;
                    g.FinalVae = primaryVae;
                    g.FinalImageOut = primaryImageOut;
                    executed.Add(parallel.Spec.Id);
                }
            }
        }
        finally
        {
            snapshot.Restore();
        }
    }

    private sealed class StageParamSnapshot : IDisposable
    {
        private readonly WorkflowGenerator _g;
        private readonly bool _hadKeep;
        private readonly bool _hadApplyAfter;
        private readonly bool _hadControl;
        private readonly bool _hadModel;
        private readonly bool _hadVae;
        private readonly bool _hadSteps;
        private readonly bool _hadCfg;
        private readonly bool _hadSampler;
        private readonly bool _hadScheduler;
        private readonly bool _keep;
        private readonly string _applyAfter;
        private readonly double _control;
        private readonly string _model;
        private readonly T2IModel _vae;
        private readonly int _steps;
        private readonly double _cfg;
        private readonly string _sampler;
        private readonly string _scheduler;

        public StageParamSnapshot(WorkflowGenerator g)
        {
            _g = g;
            _hadKeep = g.UserInput.TryGet(Base2EditExtension.KeepPreEditImage, out _keep);
            _hadApplyAfter = g.UserInput.TryGet(Base2EditExtension.ApplyEditAfter, out _applyAfter);
            _hadControl = g.UserInput.TryGet(Base2EditExtension.EditControl, out _control);
            _hadModel = g.UserInput.TryGet(Base2EditExtension.EditModel, out _model);
            _hadVae = g.UserInput.TryGet(Base2EditExtension.EditVAE, out _vae);
            _hadSteps = g.UserInput.TryGet(Base2EditExtension.EditSteps, out _steps);
            _hadCfg = g.UserInput.TryGet(Base2EditExtension.EditCFGScale, out _cfg);
            _hadSampler = g.UserInput.TryGet(Base2EditExtension.EditSampler, out _sampler);
            _hadScheduler = g.UserInput.TryGet(Base2EditExtension.EditScheduler, out _scheduler);
        }

        public void Restore()
        {
            if (_hadKeep) _g.UserInput.Set(Base2EditExtension.KeepPreEditImage, _keep); else _g.UserInput.Remove(Base2EditExtension.KeepPreEditImage);
            if (_hadApplyAfter) _g.UserInput.Set(Base2EditExtension.ApplyEditAfter, _applyAfter); else _g.UserInput.Remove(Base2EditExtension.ApplyEditAfter);
            if (_hadControl) _g.UserInput.Set(Base2EditExtension.EditControl, _control); else _g.UserInput.Remove(Base2EditExtension.EditControl);
            if (_hadModel) _g.UserInput.Set(Base2EditExtension.EditModel, _model); else _g.UserInput.Remove(Base2EditExtension.EditModel);
            if (_hadVae) _g.UserInput.Set(Base2EditExtension.EditVAE, _vae); else _g.UserInput.Remove(Base2EditExtension.EditVAE);
            if (_hadSteps) _g.UserInput.Set(Base2EditExtension.EditSteps, _steps); else _g.UserInput.Remove(Base2EditExtension.EditSteps);
            if (_hadCfg) _g.UserInput.Set(Base2EditExtension.EditCFGScale, _cfg); else _g.UserInput.Remove(Base2EditExtension.EditCFGScale);
            if (_hadSampler) _g.UserInput.Set(Base2EditExtension.EditSampler, _sampler); else _g.UserInput.Remove(Base2EditExtension.EditSampler);
            if (_hadScheduler) _g.UserInput.Set(Base2EditExtension.EditScheduler, _scheduler); else _g.UserInput.Remove(Base2EditExtension.EditScheduler);
        }

        public void Dispose()
        {
        }
    }

    private static void ApplyStageOverrides(WorkflowGenerator g, StageSpec stage)
    {
        if (stage.KeepPreEditImage.HasValue)
        {
            g.UserInput.Set(Base2EditExtension.KeepPreEditImage.Type, stage.KeepPreEditImage.Value ? "true" : "false");
        }

        if (!string.IsNullOrWhiteSpace(stage.ApplyAfter))
        {
            g.UserInput.Set(Base2EditExtension.ApplyEditAfter.Type, stage.ApplyAfter);
        }

        if (stage.Control.HasValue)
        {
            g.UserInput.Set(Base2EditExtension.EditControl.Type, $"{stage.Control.Value}");
        }

        if (!string.IsNullOrWhiteSpace(stage.Model))
        {
            g.UserInput.Set(Base2EditExtension.EditModel.Type, stage.Model);
        }

        if (!string.IsNullOrWhiteSpace(stage.Vae))
        {
            g.UserInput.Set(Base2EditExtension.EditVAE.Type, stage.Vae);
        }

        if (stage.Steps.HasValue)
        {
            g.UserInput.Set(Base2EditExtension.EditSteps.Type, $"{stage.Steps.Value}");
        }

        if (stage.CfgScale.HasValue)
        {
            g.UserInput.Set(Base2EditExtension.EditCFGScale.Type, $"{stage.CfgScale.Value}");
        }

        if (!string.IsNullOrWhiteSpace(stage.Sampler))
        {
            g.UserInput.Set(Base2EditExtension.EditSampler.Type, stage.Sampler);
        }

        if (!string.IsNullOrWhiteSpace(stage.Scheduler))
        {
            g.UserInput.Set(Base2EditExtension.EditScheduler.Type, stage.Scheduler);
        }
    }
}
