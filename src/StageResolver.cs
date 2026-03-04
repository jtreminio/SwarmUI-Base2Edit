using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Image = SwarmUI.Utils.Image;

namespace Base2Edit;

/// <summary>
/// Resolves <c>&lt;b2eimage[...]&gt;</c> prompt references into latent tensors that can be fed
/// into ReferenceLatent conditioning nodes. Handles prompt images, base/refiner stage outputs,
/// and earlier edit stage outputs, performing VAE encode/decode conversions as needed when the
/// source and target VAEs differ.
/// </summary>
public class StageResolver(WorkflowGenerator g, StageRefStore store)
{
    public readonly WorkflowGenerator g = g;
    public readonly StageRefStore store = store;

    public sealed record ImageReference(
        StageRefStore.StageKind Kind,
        int Index,
        string NormalizedTarget,
        string RawTarget
    );

    /// <summary>
    /// Takes a list of parsed <c>&lt;b2eimage[...]&gt;</c> references and resolves each one to a
    /// latent tensor suitable for ReferenceLatent nodes. Deduplicates results and skips
    /// references that resolve to the current stage's own latent (which is added separately
    /// by the caller). Logs warnings for references that can't be resolved.
    /// </summary>
    public List<JArray> ResolveImageLatents(
        IReadOnlyList<ImageReference> references,
        JArray currentVae,
        int? index = null)
    {
        List<JArray> resolved = [];
        WGNodeData currentStageVae = currentVae is null
            ? null
            : new WGNodeData(currentVae, g, WGNodeData.DT_VAE, g.CurrentCompat());
        
        if (references is null || references.Count == 0)
        {
            return resolved;
        }

        foreach (ImageReference reference in references)
        {
            string warning = "";
            JArray resolvedLatent = reference.Kind switch
            {
                StageRefStore.StageKind.Prompt => ResolvePromptImageRef(reference, currentStageVae, out warning),
                StageRefStore.StageKind.Edit => ResolveEditStageRef(reference, currentStageVae, index.Value, out warning),
                StageRefStore.StageKind.Base => ResolveBaseOrRefinerRef(reference, currentStageVae, out warning),
                StageRefStore.StageKind.Refiner => ResolveBaseOrRefinerRef(reference, currentStageVae, out warning),
                _ => null
            };

            if (resolvedLatent is null)
            {
                if (!string.IsNullOrEmpty(warning))
                {
                    string stageIndex = "";
                    if (index is not null)
                    {
                        stageIndex = $" in stage {index}";
                    }

                    Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]>{stageIndex}: {warning}");
                }

                continue;
            }

            // Current-stage latent is always appended by the existing final ReferenceLatent node;
            // avoid generating a duplicate no-op chain entry when a b2eimage resolves to the same latent.
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

    /// <summary>
    /// Resolves a <c>&lt;b2eimage[promptN]&gt;</c> reference by loading the Nth prompt image
    /// and encoding it through the current stage's VAE. Reuses an existing VAEEncode node
    /// if one already matches the image+VAE pair.
    /// </summary>
    private JArray ResolvePromptImageRef(
        ImageReference reference,
        WGNodeData currentVae,
        out string warning)
    {
        warning = "";

        if (currentVae is null)
        {
            warning = "current stage VAE is unavailable.";
            return null;
        }

        if (!g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> promptImages)
            || promptImages is null
            || reference.Index < 0
            || reference.Index >= promptImages.Count)
        {
            warning = "referenced prompt image is unavailable.";
            return null;
        }

        WGNodeData loadedImage = g.LoadImage(
            img: promptImages[reference.Index],
            param: $"${{promptimages.{reference.Index}}}",
            resize: false
        );

        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, loadedImage.Path, currentVae.Path, out JArray reusedLatent))
        {
            return CloneNodeRef(reusedLatent);
        }

        return CloneNodeRef(loadedImage.AsLatentImage(currentVae).Path);
    }

    /// <summary>
    /// Resolves a <c>&lt;b2eimage[editN]&gt;</c> reference by looking up the captured output of
    /// edit stage N. Only allows references to earlier stages (lower index) to prevent
    /// circular dependencies.
    /// </summary>
    private JArray ResolveEditStageRef(
        ImageReference reference,
        WGNodeData currentVae,
        int currentIndex,
        out string warning)
    {
        warning = "";

        if (reference.Index >= currentIndex)
        {
            warning = "edit references must target an earlier stage.";
            return null;
        }

        if (!store.TryGetEditRef(reference.Index, out StageRefStore.StageRef stageRef) || stageRef is null)
        {
            warning = "referenced edit stage output is unavailable.";
            return null;
        }

        return ResolveStageRefMedia(stageRef, currentVae, out warning);
    }

    /// <summary>
    /// Resolves a <c>&lt;b2eimage[base]&gt;</c> or <c>&lt;b2eimage[refiner]&gt;</c> reference by
    /// looking up the captured pipeline state from the base or refiner phase.
    /// </summary>
    private JArray ResolveBaseOrRefinerRef(
        ImageReference reference,
        WGNodeData currentVae,
        out string warning)
    {
        warning = "";

        StageRefStore.StageRef stageRef = reference.Kind == StageRefStore.StageKind.Base
            ? store.Base
            : store.Refiner;

        return ResolveStageRefMedia(stageRef, currentVae, out warning);
    }

    /// <summary>
    /// Unwraps a <see cref="StageRefStore.StageRef"/> to get its media output and delegates
    /// to <see cref="ResolveStageMediaToLatent"/> for VAE conversion.
    /// </summary>
    private JArray ResolveStageRefMedia(
        StageRefStore.StageRef stageRef,
        WGNodeData currentVae,
        out string warning)
    {
        warning = "";

        if (stageRef is null || stageRef.Media is null)
        {
            warning = "it has no image/latent output.";
            return null;
        }

        return ResolveStageMediaToLatent(stageRef.Media, stageRef.Vae, currentVae, out warning);
    }

    /// <summary>
    /// Converts a stage's media output (image or latent) into a latent encoded with the target
    /// VAE. If the media is already a latent and the VAEs match, returns it directly. If the
    /// VAEs differ, decodes through the source VAE then re-encodes through the target VAE.
    /// Reuses existing VAEEncode/VAEDecode nodes when possible to avoid duplicates.
    /// </summary>
    private JArray ResolveStageMediaToLatent(
        WGNodeData stageMedia,
        WGNodeData sourceVae,
        WGNodeData targetVae,
        out string warning)
    {
        warning = "";
        if (stageMedia is null)
        {
            warning = "it has no image/latent output.";
            return null;
        }

        if (targetVae is null)
        {
            if (stageMedia.IsLatentData)
            {
                return CloneNodeRef(stageMedia.Path);
            }

            warning = "current stage VAE is unavailable.";
            return null;
        }

        if (!stageMedia.IsLatentData)
        {
            if (VaeNodeReuse.ReuseVaeEncodeForImage(g, stageMedia.Path, targetVae.Path, out JArray reusedEncoded))
            {
                return CloneNodeRef(reusedEncoded);
            }

            return CloneNodeRef(stageMedia.AsLatentImage(targetVae).Path);
        }

        if (sourceVae is not null && JToken.DeepEquals(sourceVae.Path, targetVae.Path))
        {
            return CloneNodeRef(stageMedia.Path);
        }

        if (sourceVae is null)
        {
            warning = "cannot VAE-convert the referenced stage output.";
            return null;
        }

        JArray imageRef = null;
        if (VaeNodeReuse.ReuseVaeDecodeForSamplesAndVae(g, stageMedia.Path, sourceVae.Path, out JArray reusedImage))
        {
            imageRef = CloneNodeRef(reusedImage);
        }
        else
        {
            imageRef = CloneNodeRef(stageMedia.AsRawImage(sourceVae).Path);
        }

        if (imageRef is null)
        {
            warning = "cannot VAE-convert the referenced stage output.";
            return null;
        }

        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, imageRef, targetVae.Path, out JArray reusedLatent))
        {
            return CloneNodeRef(reusedLatent);
        }

        WGNodeData imageNode = new(imageRef, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        return CloneNodeRef(imageNode.AsLatentImage(targetVae).Path);
    }

    /// <summary>
    /// Creates a shallow copy of a [nodeId, outputIndex] reference array so that callers
    /// get independent instances that won't be mutated by later workflow modifications.
    /// </summary>
    private static JArray CloneNodeRef(JArray nodeRef)
    {
        if (nodeRef is null || nodeRef.Count != 2)
        {
            return null;
        }

        return new JArray(nodeRef[0], nodeRef[1]);
    }
}
