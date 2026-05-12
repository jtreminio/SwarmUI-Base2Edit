using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace Base2Edit;

public static class VaeNodeReuse
{
    /// <summary>
    /// Re-target an existing unconsumed final decode node to point at new samples+VAE.
    /// This avoids leaving a dangling pre-edit decode when Base2Edit runs after the normal decode step.
    /// </summary>
    public static bool TryRetargetUnconsumedVaeDecode(
        WorkflowGenerator g,
        JArray imageRef,
        JArray intendedVaeRef,
        JArray samplesRef,
        out JArray imageOutRef)
    {
        imageOutRef = null;

        if (imageRef is null || intendedVaeRef is null || samplesRef is null || imageRef.Count != 2)
        {
            return false;
        }

        try
        {
            if ($"{imageRef[1]}" != "0")
            {
                return false;
            }

            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            ComfyNode decode = bridge.Graph.GetNode($"{imageRef[0]}");
            if (decode is not (VAEDecodeNode or VAEDecodeTiledNode))
            {
                return false;
            }

            // If this decode output already feeds any other node, treat it as in-use and do not mutate it.
            if (decode.Outputs.Count > 0
                && bridge.Graph.FindInputsConnectedTo(decode.Outputs[0]).Count > 0)
            {
                return false;
            }

            INodeOutput intendedVae = bridge.ResolvePath(intendedVaeRef);
            INodeOutput samples = bridge.ResolvePath(samplesRef);
            if (intendedVae is null || samples is null)
            {
                return false;
            }

            INodeInput samplesInput = decode.FindInput("samples") ?? decode.FindInput("latent");
            if (samplesInput is null)
            {
                return false;
            }
            decode.FindInput("vae")?.ConnectToUntyped(intendedVae);
            samplesInput.ConnectToUntyped(samples);

            imageOutRef = [decode.Id, 0];
            return true;
        }
        catch (Exception ex)
        {
            Logs.Debug($"Base2Edit: Failed to retarget existing VAEDecode node: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Find a VAEDecode node that already consumes samplesRef.
    /// </summary>
    public static bool ReuseVaeDecodeForSamples(WorkflowGenerator g, JArray samplesRef, out JArray imageOutRef)
    {
        return TryFindConsumerNode<VAEDecodeNode>(g, samplesRef, vaeRef: null, out imageOutRef,
            "reuse existing VAEDecode node");
    }

    /// <summary>
    /// Find a VAEEncode node that already consumes imageRef and uses intendedVaeRef.
    /// </summary>
    public static bool ReuseVaeEncodeForImage(WorkflowGenerator g, JArray imageRef, JArray intendedVaeRef, out JArray samplesOutRef)
    {
        return TryFindConsumerNode<VAEEncodeNode>(g, imageRef, intendedVaeRef, out samplesOutRef,
            "reuse existing VAEEncode node");
    }

    /// <summary>
    /// Find a VAEDecode node that already consumes samplesRef and uses intendedVaeRef.
    /// </summary>
    public static bool ReuseVaeDecodeForSamplesAndVae(WorkflowGenerator g, JArray samplesRef, JArray intendedVaeRef, out JArray imageOutRef)
    {
        return TryFindConsumerNode<VAEDecodeNode>(g, samplesRef, intendedVaeRef, out imageOutRef,
            "reuse existing final VAEDecode node");
    }

    /// <summary>
    /// Returns true if the workflow already has a SwarmSaveImageWS or SaveImage node
    /// connected to the given image output ref.
    /// </summary>
    public static bool HasSaveForImage(WorkflowGenerator g, JArray imageRef)
    {
        if (imageRef is null || imageRef.Count != 2)
        {
            return false;
        }

        return TryFindConsumerNode<SwarmSaveImageWSNode>(g, imageRef, vaeRef: null, out _, "check existing save for image")
            || TryFindConsumerNode<SaveImageNode>(g, imageRef, vaeRef: null, out _, "check existing save for image");
    }

    /// <summary>
    /// Searches downstream consumers of <paramref name="sourceRef"/> for a node of type
    /// <typeparamref name="T"/>. When <paramref name="vaeRef"/> is non-null, only matches
    /// nodes whose "vae" input is connected to that output.
    /// </summary>
    private static bool TryFindConsumerNode<T>(
        WorkflowGenerator g,
        JArray sourceRef,
        JArray vaeRef,
        out JArray outRef,
        string debugLabel)
        where T : ComfyNode
    {
        outRef = null;

        if (sourceRef is null)
        {
            return false;
        }

        try
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            if (bridge.ResolvePath(sourceRef) is not INodeOutput sourceOutput)
            {
                return false;
            }

            INodeOutput expectedVae = vaeRef is not null ? bridge.ResolvePath(vaeRef) : null;
            if (vaeRef is not null && expectedVae is null)
            {
                return false;
            }

            foreach ((ComfyNode node, _) in bridge.Graph.FindInputsConnectedTo(sourceOutput))
            {
                if (node is not T)
                {
                    continue;
                }

                if (expectedVae is not null)
                {
                    if (node.FindInput("vae")?.Connection is not INodeOutput vaeConn
                        || vaeConn.Node.Id != expectedVae.Node.Id
                        || vaeConn.SlotIndex != expectedVae.SlotIndex)
                    {
                        continue;
                    }
                }

                outRef = [node.Id, 0];
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"Base2Edit: Failed to {debugLabel}: {ex}");
            return false;
        }
    }
}
