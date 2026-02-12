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

        if (g?.Workflow is null || imageRef is null || intendedVaeRef is null || samplesRef is null || imageRef.Count != 2)
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
            if (classType != "VAEDecode" && classType != "VAEDecodeTiled")
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
    /// Find a VAEDecode node that already consumes samplesRef
    /// Sets imageOutRef to [nodeId, 0] when found
    /// </summary>
    public static bool ReuseVaeDecodeForSamples(WorkflowGenerator g, JArray samplesRef, out JArray imageOutRef)
    {
        imageOutRef = null;

        if (g?.Workflow is null || samplesRef is null)
        {
            return false;
        }

        try
        {
            foreach (var conn in WorkflowUtils.FindInputConnections(g.Workflow, samplesRef))
            {
                if (g.Workflow[conn.NodeId] is JObject nodeObj && $"{nodeObj["class_type"]}" == "VAEDecode")
                {
                    imageOutRef = [conn.NodeId, 0];
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"Base2Edit: Failed to reuse existing VAEDecode node: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Find a VAEDecode node that already consumes samplesRef and uses intendedVaeRef
    /// Sets samplesOutRef to [nodeId, 0] when found
    /// </summary>
    public static bool ReuseVaeEncodeForImage(WorkflowGenerator g, JArray imageRef, JArray intendedVaeRef, out JArray samplesOutRef)
    {
        samplesOutRef = null;

        if (g?.Workflow is null || imageRef is null || intendedVaeRef is null)
        {
            return false;
        }

        try
        {
            foreach (var conn in WorkflowUtils.FindInputConnections(g.Workflow, imageRef))
            {
                if (g.Workflow[conn.NodeId] is not JObject nodeObj || $"{nodeObj["class_type"]}" != "VAEEncode")
                {
                    continue;
                }

                if (nodeObj["inputs"] is not JObject inputs)
                {
                    continue;
                }

                // Only reuse an encode that matches our intended VAE (avoid subtle cross-VAE bugs).
                if (inputs.TryGetValue("vae", out JToken vaeTok) && JToken.DeepEquals(vaeTok, intendedVaeRef))
                {
                    samplesOutRef = [conn.NodeId, 0];
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"Base2Edit: Failed to reuse existing VAEEncode node: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Find a VAEDecode node that already consumes samplesRef and uses intendedVaeRef
    /// Sets imageOutRef to [nodeId, 0] when found
    /// </summary>
    public static bool ReuseVaeDecodeForSamplesAndVae(WorkflowGenerator g, JArray samplesRef, JArray intendedVaeRef, out JArray imageOutRef)
    {
        imageOutRef = null;

        if (g?.Workflow is null || samplesRef is null || intendedVaeRef is null)
        {
            return false;
        }

        try
        {
            foreach (var conn in WorkflowUtils.FindInputConnections(g.Workflow, samplesRef))
            {
                if (g.Workflow[conn.NodeId] is not JObject nodeObj || $"{nodeObj["class_type"]}" != "VAEDecode")
                {
                    continue;
                }

                if (nodeObj["inputs"] is not JObject inputs)
                {
                    continue;
                }

                if (inputs.TryGetValue("vae", out JToken vaeTok) && JToken.DeepEquals(vaeTok, intendedVaeRef))
                {
                    imageOutRef = [conn.NodeId, 0];
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"Base2Edit: Failed to reuse existing final VAEDecode node: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Returns true if the workflow already has a SwarmSaveImageWS or SaveImage node
    /// connected to the given image output ref. Used to avoid attaching multiple save
    /// nodes to the same VAEDecode output.
    /// </summary>
    public static bool HasSaveForImage(WorkflowGenerator g, JArray imageRef)
    {
        if (g?.Workflow is null || imageRef is null || imageRef.Count != 2)
        {
            return false;
        }

        try
        {
            foreach (var conn in WorkflowUtils.FindInputConnections(g.Workflow, imageRef))
            {
                if (g.Workflow[conn.NodeId] is not JObject nodeObj)
                {
                    continue;
                }

                string classType = $"{nodeObj["class_type"]}";
                if (classType == "SwarmSaveImageWS" || classType == "SaveImage")
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"Base2Edit: Failed to check existing save for image: {ex}");
            return false;
        }
    }
}
