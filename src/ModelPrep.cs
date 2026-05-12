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

    public static T2IModel TryResolveEditModel(
        WorkflowGenerator g,
        string selection,
        out bool mustReencode)
    {
        mustReencode = false;
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

    public static ModelRef LoadEditModelWithoutLoras(
        WorkflowGenerator g,
        T2IModel editModel,
        int sectionId)
    {
        using ParamSnapshot snapshot = SnapshotLoraParams(g);
        snapshot.Remove();
        (T2IModel _, WGNodeData modelNode, WGNodeData clipNode, WGNodeData vaeNode) =
            g.CreateModelLoader(editModel, "Edit", sectionId: sectionId);

        return new ModelRef(modelNode.Path, clipNode.Path, vaeNode.Path);
    }

    private static T2IModel ResolveEditModel(string selection, T2IModel baseModel, T2IModel refinerModel)
    {
        static bool MatchesSelection(T2IModel model, string sel)
        {
            if (model is null || string.IsNullOrWhiteSpace(sel))
            {
                return false;
            }

            if (StringUtils.Equals(model.Name, sel))
            {
                return true;
            }

            return StringUtils.Equals(T2IParamTypes.CleanModelName(model.Name), sel);
        }

        if (StringUtils.Equals(selection, UseBase))
        {
            return baseModel;
        }

        if (StringUtils.Equals(selection, UseRefiner))
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
