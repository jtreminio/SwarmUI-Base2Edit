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
            foreach (ResolvedStage st in resolved.OrderBy(s => s.Spec.Id))
            {
                if (st.Hook != currentHook)
                {
                    continue;
                }

                if (st.DependsOnStageId.HasValue && !executed.Contains(st.DependsOnStageId.Value))
                {
                    // This can only happen if a stage depends on a stage in a different hook
                    throw new SwarmReadableErrorException(
                        $"Base2Edit: Edit Stage {st.Spec.Id} depends on Edit Stage {st.DependsOnStageId.Value} which is not executed in this phase."
                    );
                }

                ApplyStageOverrides(g, st.Spec);
                RunEditStage(g, isFinalStep: isFinalStep, stageIndex: st.Spec.Id);
                executed.Add(st.Spec.Id);
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
