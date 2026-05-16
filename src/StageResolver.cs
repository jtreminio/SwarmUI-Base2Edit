using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using ComfyTyped.Core;
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

    public List<JArray> ResolveImagePixels(
        IReadOnlyList<ImageReference> references,
        int? index = null)
    {
        List<JArray> resolved = [];

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

            JArray resolvedPixels = MediaToPixels(media, sourceVae, out warning);
            if (resolvedPixels is null)
            {
                if (!string.IsNullOrEmpty(warning))
                {
                    LogWarn(reference, index, warning);
                }
                continue;
            }

            bool alreadyAdded = false;
            foreach (JArray existing in resolved)
            {
                if (JToken.DeepEquals(existing, resolvedPixels))
                {
                    alreadyAdded = true;
                    break;
                }
            }

            if (!alreadyAdded)
            {
                resolved.Add(resolvedPixels);
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
            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, media.Path, targetVae.Path, out INodeOutput reusedEncoded))
            {
                return CloneNodeRef(WorkflowBridge.ToPath(reusedEncoded));
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
        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, pixels.Path, targetVae.Path, out INodeOutput reusedLatent))
        {
            return CloneNodeRef(WorkflowBridge.ToPath(reusedLatent));
        }

        return CloneNodeRef(pixels.AsLatentImage(targetVae).Path);
    }

    private JArray MediaToPixels(WGNodeData media, WGNodeData sourceVae, out string warning)
    {
        warning = "";

        if (!media.IsLatentData)
        {
            return CloneNodeRef(media.Path);
        }

        if (sourceVae is null)
        {
            warning = "cannot decode the referenced stage output (no source VAE).";
            return null;
        }

        return CloneNodeRef(ToPixels(media, sourceVae).Path);
    }

    private WGNodeData ToPixels(WGNodeData latent, WGNodeData vae) =>
        TryFindExistingImageNode(g, latent) ?? latent.AsRawImage(vae);

    public static JArray TryFindExistingImage(WorkflowGenerator g, WGNodeData media)
    {
        if (media is null)
        {
            return null;
        }
        if (media.IsRawMedia)
        {
            return media.Path;
        }
        if (!media.IsLatentData || media.Path is null || media.Path.Count != 2)
        {
            return null;
        }

        if (g.Workflow[$"{media.Path[0]}"] is JObject latentNode
            && (string)latentNode["class_type"] is string ct
            && (ct == "VAEEncode" || ct == "VAEEncodeTiled")
            && latentNode["inputs"] is JObject latentInputs
            && latentInputs["pixels"] is JArray pixels
            && pixels.Count == 2)
        {
            return (JArray)pixels.DeepClone();
        }

        if (VaeNodeReuse.ReuseVaeDecodeForSamples(g, media.Path, out INodeOutput reusedImage))
        {
            return WorkflowBridge.ToPath(reusedImage);
        }

        return null;
    }

    public static WGNodeData TryFindExistingImageNode(WorkflowGenerator g, WGNodeData media)
    {
        JArray path = TryFindExistingImage(g, media);
        if (path is null)
        {
            return null;
        }
        if (media is not null && JToken.DeepEquals(path, media.Path))
        {
            return media;
        }
        return new WGNodeData(path, g, WGNodeData.DT_IMAGE, g.CurrentCompat())
        {
            Width = media?.Width,
            Height = media?.Height
        };
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
