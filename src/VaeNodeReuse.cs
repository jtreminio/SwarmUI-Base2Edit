using System;
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
            string nodeId = $"{imageRef[0]}";
            string outIdx = $"{imageRef[1]}";
            if (outIdx != "0")
            {
                return false;
            }

            if (g.Workflow[nodeId] is not JObject nodeObj)
            {
                return false;
            }

            string classType = $"{nodeObj["class_type"]}";
            if (classType != NodeTypes.VAEDecode && classType != NodeTypes.VAEDecodeTiled)
            {
                return false;
            }

            // If this decode output already feeds any other node, treat it as in-use and do not mutate it.
            if (WorkflowUtils.FindInputConnections(g.Workflow, imageRef).Count > 0)
            {
                return false;
            }

            if (nodeObj["inputs"] is not JObject inputs)
            {
                return false;
            }

            inputs["vae"] = new JArray(intendedVaeRef[0], intendedVaeRef[1]);
            if (inputs.ContainsKey("samples"))
            {
                inputs["samples"] = new JArray(samplesRef[0], samplesRef[1]);
            }
            else if (inputs.ContainsKey("latent"))
            {
                inputs["latent"] = new JArray(samplesRef[0], samplesRef[1]);
            }
            else
            {
                return false;
            }

            imageOutRef = [nodeId, 0];
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
        return TryFindConsumerNode(g, samplesRef, NodeTypes.VAEDecode, vaeRef: null, out imageOutRef,
            "reuse existing VAEDecode node");
    }

    /// <summary>
    /// Find a VAEEncode node that already consumes imageRef and uses intendedVaeRef.
    /// </summary>
    public static bool ReuseVaeEncodeForImage(WorkflowGenerator g, JArray imageRef, JArray intendedVaeRef, out JArray samplesOutRef)
    {
        return TryFindConsumerNode(g, imageRef, NodeTypes.VAEEncode, intendedVaeRef, out samplesOutRef,
            "reuse existing VAEEncode node");
    }

    /// <summary>
    /// Find a VAEDecode node that already consumes samplesRef and uses intendedVaeRef.
    /// </summary>
    public static bool ReuseVaeDecodeForSamplesAndVae(WorkflowGenerator g, JArray samplesRef, JArray intendedVaeRef, out JArray imageOutRef)
    {
        return TryFindConsumerNode(g, samplesRef, NodeTypes.VAEDecode, intendedVaeRef, out imageOutRef,
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

        return TryFindConsumerNode(g, imageRef, NodeTypes.SwarmSaveImageWS, vaeRef: null, out _, "check existing save for image")
            || TryFindConsumerNode(g, imageRef, NodeTypes.SaveImage, vaeRef: null, out _, "check existing save for image");
    }

    /// <summary>
    /// Searches downstream consumers of <paramref name="sourceRef"/> for a node matching
    /// <paramref name="classType"/>. When <paramref name="vaeRef"/> is non-null, only matches
    /// nodes whose "vae" input equals the given ref.
    /// </summary>
    private static bool TryFindConsumerNode(
        WorkflowGenerator g,
        JArray sourceRef,
        string classType,
        JArray vaeRef,
        out JArray outRef,
        string debugLabel)
    {
        outRef = null;

        if (sourceRef is null)
        {
            return false;
        }

        try
        {
            foreach (var conn in WorkflowUtils.FindInputConnections(g.Workflow, sourceRef))
            {
                if (g.Workflow[conn.NodeId] is not JObject nodeObj || $"{nodeObj["class_type"]}" != classType)
                {
                    continue;
                }

                if (vaeRef is not null)
                {
                    if (nodeObj["inputs"] is not JObject inputs
                        || !inputs.TryGetValue("vae", out JToken vaeTok)
                        || !JToken.DeepEquals(vaeTok, vaeRef))
                    {
                        continue;
                    }
                }

                outRef = [conn.NodeId, 0];
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
