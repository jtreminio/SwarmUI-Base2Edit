using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace Base2Edit;

public static class EditStageModelPreparation
{
    public static bool TryResolveEditModel(
        WorkflowGenerator g,
        T2IRegisteredParam<string> editModelParam,
        out T2IModel editModel,
        out bool mustReencode
    ) {
        mustReencode = false;
        editModel = null;
        if (g is null) {
            return false;
        }

        string selection = g.UserInput.Get(editModelParam, "(Use Refiner)");
        T2IModel baseModel = g.UserInput.Get(T2IParamTypes.Model, null);
        T2IModel refinerModel = g.UserInput.TryGet(T2IParamTypes.RefinerModel, out T2IModel rm) && rm is not null
            ? rm
            : baseModel;

        editModel = ResolveSdModel(selection, baseModel, refinerModel);
        if (editModel is null)
        {
            return false;
        }

        T2IModel current = g.FinalLoadedModel ?? (g.IsRefinerStage ? refinerModel : baseModel);
        mustReencode = editModel.ModelClass?.CompatClass != current?.ModelClass?.CompatClass;

        // Ensure downstream steps that consult FinalLoadedModel reflect the model actually used for edit
        g.FinalLoadedModel = editModel;
        g.FinalLoadedModelList = [editModel];
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

    public static (JArray Model, JArray Clip, JArray Vae) LoadEditModelWithoutLoras(
        WorkflowGenerator g,
        T2IModel editModel,
        int sectionId
    ) {
        LoraParamSnapshot snapshot = new(g);
        try
        {
            snapshot.Remove();
            (T2IModel _, JArray model, JArray clip, JArray vae) =
                g.CreateStandardModelLoader(editModel, "Edit", sectionId: sectionId);
            return (model, clip, vae);
        }
        finally
        {
            snapshot.RestoreIfHad();
        }
    }

    private static T2IModel ResolveSdModel(string selection, T2IModel baseModel, T2IModel refinerModel)
    {
        if (string.Equals(selection, "(Use Base)", StringComparison.OrdinalIgnoreCase))
        {
            return baseModel;
        }

        if (string.Equals(selection, "(Use Refiner)", StringComparison.OrdinalIgnoreCase))
        {
            return refinerModel;
        }

        if (baseModel is not null && string.Equals(baseModel.Name, selection, StringComparison.OrdinalIgnoreCase))
        {
            return baseModel;
        }

        if (refinerModel is not null && string.Equals(refinerModel.Name, selection, StringComparison.OrdinalIgnoreCase))
        {
            return refinerModel;
        }

        if (Program.T2IModelSets is not null
            && Program.T2IModelSets.TryGetValue("Stable-Diffusion", out T2IModelHandler handler)
            && handler is not null
        ) {
            if (handler.Models.TryGetValue(selection, out T2IModel direct))
            {
                return direct;
            }

            // Fallback: case-insensitive match against known models.
            foreach ((string _, T2IModel model) in handler.Models)
            {
                if (model is not null && string.Equals(model.Name, selection, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }
        }

        return null;
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
