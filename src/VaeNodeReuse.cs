using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

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
        imageOut = null;
        if (samplesRef is null)
        {
            return false;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.ResolvePath(samplesRef) is not INodeOutput samples)
        {
            return false;
        }

        foreach ((ComfyNode node, _) in bridge.Graph.FindInputsConnectedTo(samples))
        {
            if (node is VAEDecodeNode dec)
            {
                imageOut = dec.IMAGE;
                return true;
            }
        }
        return false;
    }

    public static bool ReuseVaeEncodeForImage(WorkflowGenerator g, JArray imageRef, JArray intendedVaeRef, out INodeOutput samplesOut)
    {
        samplesOut = null;
        if (imageRef is null || intendedVaeRef is null)
        {
            return false;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.ResolvePath(imageRef) is not INodeOutput pixels)
        {
            return false;
        }
        if (bridge.ResolvePath(intendedVaeRef) is not INodeOutput vae)
        {
            return false;
        }

        foreach ((ComfyNode node, _) in bridge.Graph.FindInputsConnectedTo(pixels))
        {
            if (node is VAEEncodeNode enc
                && enc.Vae.Connection is INodeOutput v
                && v.Node.Id == vae.Node.Id
                && v.SlotIndex == vae.SlotIndex)
            {
                samplesOut = enc.LATENT;
                return true;
            }
        }
        return false;
    }

    public static bool ReuseVaeDecodeForSamplesAndVae(WorkflowGenerator g, JArray samplesRef, JArray intendedVaeRef, out INodeOutput imageOut)
    {
        imageOut = null;
        if (samplesRef is null || intendedVaeRef is null)
        {
            return false;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.ResolvePath(samplesRef) is not INodeOutput samples)
        {
            return false;
        }
        if (bridge.ResolvePath(intendedVaeRef) is not INodeOutput vae)
        {
            return false;
        }

        foreach ((ComfyNode node, _) in bridge.Graph.FindInputsConnectedTo(samples))
        {
            if (node is VAEDecodeNode dec
                && dec.Vae.Connection is INodeOutput v
                && v.Node.Id == vae.Node.Id
                && v.SlotIndex == vae.SlotIndex)
            {
                imageOut = dec.IMAGE;
                return true;
            }
        }
        return false;
    }

    public static bool HasSaveForImage(WorkflowGenerator g, JArray imageRef)
    {
        if (imageRef is null)
        {
            return false;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (bridge.ResolvePath(imageRef) is not INodeOutput image)
        {
            return false;
        }

        foreach ((ComfyNode node, _) in bridge.Graph.FindInputsConnectedTo(image))
        {
            if (node is SwarmSaveImageWSNode or SaveImageNode)
            {
                return true;
            }
        }
        return false;
    }
}
