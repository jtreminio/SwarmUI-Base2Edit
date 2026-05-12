using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using ComfyTyped.Generated;
using Image = SwarmUI.Utils.Image;

namespace Base2Edit;

public class StageResolver(WorkflowGenerator g, StageRefStore store)
{
    public sealed record ImageReference(
        StageRefStore.StageKind Kind,
        int Index,
        string NormalizedTarget,
        string RawTarget
    );

    public List<JArray> ResolveImageLatents(
        IReadOnlyList<ImageReference> references,
        JArray currentVae,
        int? index = null)
    {
        List<JArray> resolved = [];
        WGNodeData targetVae = currentVae is null
            ? null
            : new WGNodeData(currentVae, g, WGNodeData.DT_VAE, g.CurrentCompat());

        if (references is null || references.Count == 0)
        {
            return resolved;
        }

        foreach (ImageReference reference in references)
        {
            if (!TryResolveSource(reference, index, out WGNodeData media, out WGNodeData sourceVae, out string warning))
            {
                if (!string.IsNullOrEmpty(warning))
                {
                    LogWarn(reference, index, warning);
                }
                continue;
            }

            JArray resolvedLatent = MediaToLatent(media, sourceVae, targetVae, out warning);
            if (resolvedLatent is null)
            {
                if (!string.IsNullOrEmpty(warning))
                {
                    LogWarn(reference, index, warning);
                }
                continue;
            }

            WGNodeData currentStageLatent = WGNodeDataUtil.TryGetCurrentLatent(g);
            if (currentStageLatent is not null && JToken.DeepEquals(resolvedLatent, currentStageLatent.Path))
            {
                continue;
            }

            bool alreadyAdded = false;
            foreach (JArray existing in resolved)
            {
                if (JToken.DeepEquals(existing, resolvedLatent))
                {
                    alreadyAdded = true;
                    break;
                }
            }

            if (!alreadyAdded)
            {
                resolved.Add(resolvedLatent);
            }
        }

        return resolved;
    }

    private bool TryResolveSource(
        ImageReference reference,
        int? currentIndex,
        out WGNodeData media,
        out WGNodeData sourceVae,
        out string warning)
    {
        media = null;
        sourceVae = null;
        warning = "";

        switch (reference.Kind)
        {
            case StageRefStore.StageKind.Prompt:
                if (!g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> promptImages)
                    || promptImages is null
                    || reference.Index < 0
                    || reference.Index >= promptImages.Count)
                {
                    warning = "referenced prompt image is unavailable.";
                    return false;
                }
                media = g.LoadImage(
                    img: promptImages[reference.Index],
                    param: $"${{promptimages.{reference.Index}}}",
                    resize: false);
                return true;

            case StageRefStore.StageKind.Edit:
                if (currentIndex is null)
                {
                    return false;
                }
                if (reference.Index >= currentIndex.Value)
                {
                    warning = "edit references must target an earlier stage.";
                    return false;
                }
                if (!store.TryGetEditRef(reference.Index, out StageRefStore.StageRef editRef) || editRef is null)
                {
                    warning = "referenced edit stage output is unavailable.";
                    return false;
                }
                if (editRef.Media is null)
                {
                    warning = "it has no image/latent output.";
                    return false;
                }
                media = editRef.Media;
                sourceVae = editRef.Vae;
                return true;

            case StageRefStore.StageKind.Base:
            case StageRefStore.StageKind.Refiner:
                StageRefStore.StageRef stageRef = reference.Kind == StageRefStore.StageKind.Base
                    ? store.Base
                    : store.Refiner;
                if (stageRef is null || stageRef.Media is null)
                {
                    warning = "it has no image/latent output.";
                    return false;
                }
                media = stageRef.Media;
                sourceVae = stageRef.Vae;
                return true;

            default:
                return false;
        }
    }

    private JArray MediaToLatent(
        WGNodeData media,
        WGNodeData sourceVae,
        WGNodeData targetVae,
        out string warning)
    {
        warning = "";

        if (targetVae is null && media.IsLatentData)
        {
            return CloneNodeRef(media.Path);
        }

        if (targetVae is null)
        {
            warning = "current stage VAE is unavailable.";
            return null;
        }

        if (!media.IsLatentData)
        {
            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, media.Path, targetVae.Path, out JArray reusedEncoded))
            {
                return CloneNodeRef(reusedEncoded);
            }

            return CloneNodeRef(media.AsLatentImage(targetVae).Path);
        }

        if (sourceVae is not null && JToken.DeepEquals(sourceVae.Path, targetVae.Path))
        {
            return CloneNodeRef(media.Path);
        }

        if (sourceVae is null)
        {
            warning = "cannot VAE-convert the referenced stage output.";
            return null;
        }

        WGNodeData pixels = ToPixels(media, sourceVae);
        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, pixels.Path, targetVae.Path, out JArray reusedLatent))
        {
            return CloneNodeRef(reusedLatent);
        }

        return CloneNodeRef(pixels.AsLatentImage(targetVae).Path);
    }

    private WGNodeData ToPixels(WGNodeData latent, WGNodeData vae)
    {
        (string sourceType, JObject sourceInputs) = latent.SourceNodeData;
        if ((sourceType == VAEEncodeNode.ClassType || sourceType == VAEEncodeTiledNode.ClassType)
            && sourceInputs?["vae"] is JArray encodeVaePath
            && vae.Path is JArray vaePath
            && encodeVaePath.Count > 0
            && vaePath.Count > 0
            && $"{encodeVaePath[0]}" == $"{vaePath[0]}"
            && sourceInputs["pixels"] is JArray pixelsPath)
        {
            return latent.WithPath(pixelsPath, WGNodeData.DT_IMAGE);
        }

        if (VaeNodeReuse.ReuseVaeDecodeForSamplesAndVae(g, latent.Path, vae.Path, out JArray reusedImage))
        {
            return latent.WithPath(reusedImage, WGNodeData.DT_IMAGE);
        }

        return latent.AsRawImage(vae);
    }

    private static void LogWarn(ImageReference reference, int? index, string warning)
    {
        string stageIndex = "";
        if (index is not null)
        {
            stageIndex = $" in stage {index}";
        }

        Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]>{stageIndex}: {warning}");
    }

    private static JArray CloneNodeRef(JArray nodeRef)
    {
        if (nodeRef is null || nodeRef.Count != 2)
        {
            return null;
        }

        return new JArray(nodeRef[0], nodeRef[1]);
    }
}
