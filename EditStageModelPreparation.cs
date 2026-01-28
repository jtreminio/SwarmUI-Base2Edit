using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace Base2Edit;

public static class EditStageModelPreparation
{
    public static bool TryResolveEditModel(
        WorkflowGenerator g,
        T2IRegisteredParam<T2IModel> editModelParam,
        out T2IModel editModel,
        out bool mustReencode
    ) {
        mustReencode = false;
        editModel = null;
        if (g is null) {
            return false;
        }

        bool hasAltEditModel = g.UserInput.TryGet(editModelParam, out T2IModel altEditModel) && altEditModel is not null;
        editModel = hasAltEditModel
            ? altEditModel
            : (g.FinalLoadedModel ?? g.UserInput.Get(T2IParamTypes.Model, null));

        if (editModel is null)
        {
            return false;
        }

        if (hasAltEditModel)
        {
            mustReencode = altEditModel.ModelClass?.CompatClass != g.FinalLoadedModel?.ModelClass?.CompatClass;
            g.FinalLoadedModel = altEditModel;
            g.FinalLoadedModelList = [altEditModel];
        }

        return true;
    }

    public static (JArray Model, JArray Clip, JArray Vae) LoadEditModelWithIsolatedLoras(
        WorkflowGenerator g,
        T2IModel editModel,
        int sectionId,
        Func<WorkflowGenerator, (List<string> Loras, List<string> Weights, List<string> TencWeights)> getEditPromptLoras
    ) {
        LoraParamSnapshot snapshot = new(g);
        try
        {
            snapshot.Remove();

            (T2IModel _, JArray model, JArray clip, JArray vae) =
                g.CreateStandardModelLoader(editModel, "Edit", sectionId: sectionId);

            (List<string> editLoras, List<string> editWeights, List<string> editTencWeights) = getEditPromptLoras(g);
            if (editLoras.Count == 0) {
                return (model, clip, vae);
            }

            try
            {
                List<string> confinements = [.. System.Linq.Enumerable.Repeat($"{sectionId}", editLoras.Count)];
                g.UserInput.Set(T2IParamTypes.Loras, editLoras);
                g.UserInput.Set(T2IParamTypes.LoraWeights, editWeights);
                g.UserInput.Set(T2IParamTypes.LoraTencWeights, editTencWeights);
                g.UserInput.Set(T2IParamTypes.LoraSectionConfinement, confinements);
                (model, clip) = g.LoadLorasForConfinement(sectionId, model, clip);
            }
            finally
            {
                // Restore the original LoRA selections immediately
                snapshot.RestoreOrRemove();
            }

            return (model, clip, vae);
        }
        finally
        {
            // Restore original LoRA selections after the "clean" edit model load
            snapshot.RestoreIfHad();
        }
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

        public void RestoreIfHad()
        {
            if (_hadLoras) _g.UserInput.Set(T2IParamTypes.Loras, _loras);
            if (_hadWeights) _g.UserInput.Set(T2IParamTypes.LoraWeights, _weights);
            if (_hadTencWeights) _g.UserInput.Set(T2IParamTypes.LoraTencWeights, _tencWeights);
            if (_hadConfinements) _g.UserInput.Set(T2IParamTypes.LoraSectionConfinement, _confinements);
        }
    }
}
