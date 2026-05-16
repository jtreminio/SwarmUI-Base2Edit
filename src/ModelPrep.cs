using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public static class ModelPrep
{
    public const string UseBase = "(Use Base)";
    public const string UseRefiner = "(Use Refiner)";

    public sealed record ModelRef(
        WGNodeData Model,
        WGNodeData Clip,
        WGNodeData Vae
    );

    public static (T2IModel Model, ModelSource Source) ResolveSelection(
        WorkflowGenerator g,
        string selection,
        string locationPrefix)
    {
        T2IModel baseModel = g.UserInput.Get(T2IParamTypes.Model, null);
        T2IModel refinerModel = g.UserInput.Get(T2IParamTypes.RefinerModel, baseModel);

        if (StringUtils.Equals(selection, UseBase))
        {
            return (baseModel, ModelSource.Base);
        }
        if (StringUtils.Equals(selection, UseRefiner))
        {
            return (refinerModel, ModelSource.Refiner);
        }

        T2IModel direct = LookupSpecific(selection, baseModel, refinerModel);
        if (direct is null)
        {
            throw new SwarmUserErrorException(
                $"Base2Edit: {locationPrefix} references unknown Model '{selection}'.");
        }
        return (direct, ModelSource.Specific);
    }

    public static bool RegisterAsFinalLoaded(WorkflowGenerator g, T2IModel editModel)
    {
        T2IModel baseModel = g.UserInput.Get(T2IParamTypes.Model, null);
        T2IModel refinerModel = g.UserInput.Get(T2IParamTypes.RefinerModel, baseModel);
        T2IModel current = g.FinalLoadedModel ?? (g.IsRefinerStage ? refinerModel : baseModel);
        bool mustReencode = editModel.ModelClass?.CompatClass != current?.ModelClass?.CompatClass;
        g.FinalLoadedModel = editModel;
        g.FinalLoadedModelList = [editModel];
        return mustReencode;
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

        return new ModelRef(modelNode, clipNode, vaeNode);
    }

    private static T2IModel LookupSpecific(string selection, T2IModel baseModel, T2IModel refinerModel)
    {
        if (MatchesSelection(baseModel, selection))
        {
            return baseModel;
        }
        if (MatchesSelection(refinerModel, selection))
        {
            return refinerModel;
        }
        if (!Program.T2IModelSets.TryGetValue("Stable-Diffusion", out T2IModelHandler handler))
        {
            return null;
        }
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

    private static bool MatchesSelection(T2IModel model, string sel)
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

    internal static ParamSnapshot SnapshotLoraParams(WorkflowGenerator g) =>
        ParamSnapshot.Of(g.UserInput,
            T2IParamTypes.Loras.Type,
            T2IParamTypes.LoraWeights.Type,
            T2IParamTypes.LoraTencWeights.Type,
            T2IParamTypes.LoraSectionConfinement.Type
        );
}
