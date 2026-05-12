using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace Base2Edit;

public static class VaeNodeReuse
{
    public static bool TryRetargetUnconsumedVaeDecode(
        WorkflowGenerator g,
        JArray imageRef,
        JArray intendedVaeRef,
        JArray samplesRef,
        out INodeOutput imageOut)
    {
        imageOut = null;

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

            INodeOutput intendedVae = bridge.ResolvePath(intendedVaeRef);
            INodeOutput samples = bridge.ResolvePath(samplesRef);
            if (intendedVae is null || samples is null)
            {
                return false;
            }

            if (decode is VAEDecodeNode typedDecode)
            {
                if (bridge.Graph.FindInputsConnectedTo(typedDecode.IMAGE).Count > 0)
                {
                    return false;
                }
                return TryRetarget(typedDecode.Samples, typedDecode.Vae, typedDecode.IMAGE,
                    intendedVae, samples, out imageOut);
            }

            if (decode is VAEDecodeTiledNode typedTiled)
            {
                if (bridge.Graph.FindInputsConnectedTo(typedTiled.IMAGE).Count > 0)
                {
                    return false;
                }
                return TryRetarget(typedTiled.Samples, typedTiled.Vae, typedTiled.IMAGE,
                    intendedVae, samples, out imageOut);
            }

            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"Base2Edit: Failed to retarget existing VAEDecode node: {ex}");
            return false;
        }
    }

    private static bool TryRetarget(
        NodeInput<LatentType> samplesSlot,
        NodeInput<VaeType> vaeSlot,
        NodeOutput<ImageType> imageSlot,
        INodeOutput intendedVae,
        INodeOutput samples,
        out INodeOutput imageOut)
    {
        vaeSlot.TryConnectToUntyped(intendedVae);
        samplesSlot.ConnectToUntyped(samples);
        imageOut = imageSlot;
        return true;
    }

    public static bool ReuseVaeDecodeForSamples(WorkflowGenerator g, JArray samplesRef, out INodeOutput imageOut)
    {
        return TryFindConsumerNode<VAEDecodeNode>(g, samplesRef, vaeRef: null, out imageOut,
            "reuse existing VAEDecode node");
    }

    public static bool ReuseVaeEncodeForImage(WorkflowGenerator g, JArray imageRef, JArray intendedVaeRef, out INodeOutput samplesOut)
    {
        return TryFindConsumerNode<VAEEncodeNode>(g, imageRef, intendedVaeRef, out samplesOut,
            "reuse existing VAEEncode node");
    }

    public static bool ReuseVaeDecodeForSamplesAndVae(WorkflowGenerator g, JArray samplesRef, JArray intendedVaeRef, out INodeOutput imageOut)
    {
        return TryFindConsumerNode<VAEDecodeNode>(g, samplesRef, intendedVaeRef, out imageOut,
            "reuse existing final VAEDecode node");
    }

    public static bool HasSaveForImage(WorkflowGenerator g, JArray imageRef)
    {
        if (imageRef is null || imageRef.Count != 2)
        {
            return false;
        }

        return TryFindConsumerNode<SwarmSaveImageWSNode>(g, imageRef, vaeRef: null, out _, "check existing save for image")
            || TryFindConsumerNode<SaveImageNode>(g, imageRef, vaeRef: null, out _, "check existing save for image");
    }

    private static bool TryFindConsumerNode<T>(
        WorkflowGenerator g,
        JArray sourceRef,
        JArray vaeRef,
        out INodeOutput outRef,
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

                outRef = node.FindOutput(0);
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
