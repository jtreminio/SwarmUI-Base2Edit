using System;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace Base2Edit;

public static class VaeNodeReuse
{
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
}
