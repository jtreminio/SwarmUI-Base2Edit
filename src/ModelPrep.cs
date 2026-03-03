using System;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace Base2Edit;

public static class ModelPrep
{
    public const string UseBase = "(Use Base)";
    public const string UseRefiner = "(Use Refiner)";

    public sealed record ModelRef(
        JArray Model,
        JArray Clip,
        JArray Vae
    );

    /// <summary>
    /// Resolves the edit stage's model selection string to a T2IModel. Handles "(Use Base)",
    /// "(Use Refiner)", and explicit model names. Sets mustReencode when the edit model's
    /// compat class differs from the currently loaded model. Also updates
    /// FinalLoadedModel/FinalLoadedModelList so downstream SwarmUI code sees the correct
    /// model for metadata and compatibility checks.
    /// </summary>
    public static T2IModel TryResolveEditModel(
        WorkflowGenerator g,
        T2IRegisteredParam<string> editModelParam,
        out bool mustReencode)
    {
        mustReencode = false;
        string selection = g.UserInput.Get(Base2EditExtension.EditModel, UseRefiner);
        T2IModel baseModel = g.UserInput.Get(T2IParamTypes.Model, null);
        T2IModel refinerModel = g.UserInput.Get(T2IParamTypes.RefinerModel, baseModel);
        T2IModel editModel = ResolveEditModel(selection, baseModel, refinerModel);

        if (editModel is null)
        {
            return null;
        }

        T2IModel current = g.FinalLoadedModel ?? (g.IsRefinerStage ? refinerModel : baseModel);
        mustReencode = editModel.ModelClass?.CompatClass != current?.ModelClass?.CompatClass;

        g.FinalLoadedModel = editModel;
        g.FinalLoadedModelList = [editModel];

        return editModel;
    }

    /// <summary>
    /// Creates a model loader node for the edit model without any LoRAs applied. Temporarily
    /// strips all LoRA params from UserInput so CreateModelLoader emits a clean checkpoint load,
    /// then restores the original LoRA state. Apply stage-specific and global LoRAs afterward
    /// via LoadLorasForConfinement.
    /// </summary>
    public static ModelRef LoadEditModelWithoutLoras(
        WorkflowGenerator g,
        T2IModel editModel,
        int sectionId)
    {
        ParamSnapshot snapshot = SnapshotLoraParams(g);

        try
        {
            snapshot.Remove();
            (T2IModel _, WGNodeData modelNode, WGNodeData clipNode, WGNodeData vaeNode) =
                g.CreateModelLoader(editModel, "Edit", sectionId: sectionId);

            return new ModelRef(modelNode.Path, clipNode.Path, vaeNode.Path);
        }
        finally
        {
            snapshot.Reset();
        }
    }

    /// <summary>
    /// Maps a model selection string to a T2IModel. Tries in order: "(Use Base)" / "(Use Refiner)"
    /// keywords, exact match against base/refiner model names, then a full scan of the
    /// Stable-Diffusion model registry for explicit model names.
    /// </summary>
    private static T2IModel ResolveEditModel(string selection, T2IModel baseModel, T2IModel refinerModel)
    {
        static bool MatchesSelection(T2IModel model, string sel)
        {
            if (model is null || string.IsNullOrWhiteSpace(sel))
            {
                return false;
            }

            if (string.Equals(model.Name, sel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(T2IParamTypes.CleanModelName(model.Name), sel, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(selection, UseBase, StringComparison.OrdinalIgnoreCase))
        {
            return baseModel;
        }

        if (string.Equals(selection, UseRefiner, StringComparison.OrdinalIgnoreCase))
        {
            return refinerModel;
        }

        if (MatchesSelection(baseModel, selection))
        {
            return baseModel;
        }

        if (MatchesSelection(refinerModel, selection))
        {
            return refinerModel;
        }

        Program.T2IModelSets.TryGetValue("Stable-Diffusion", out T2IModelHandler handler);

        if (handler.Models.TryGetValue(selection, out T2IModel direct))
        {
            return direct;
        }

        // Fallback: match against model name or cleaned display name
        foreach ((string _, T2IModel model) in handler.Models)
        {
            if (MatchesSelection(model, selection))
            {
                return model;
            }
        }

        return null;
    }

    internal static ParamSnapshot SnapshotLoraParams(WorkflowGenerator g) =>
        ParamSnapshot.Of(g.UserInput,
            T2IParamTypes.Loras.Type,
            T2IParamTypes.LoraWeights.Type,
            T2IParamTypes.LoraTencWeights.Type,
            T2IParamTypes.LoraSectionConfinement.Type
        );
}
