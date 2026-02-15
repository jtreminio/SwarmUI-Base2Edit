using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Base2Edit;

public readonly record struct WorkflowNode(string Id, JObject Node);

public readonly record struct WorkflowInputConnection(string NodeId, string InputName, JArray Connection);

public static class WorkflowUtils
{
    private static readonly string[] SpatialTraceInputKeys =
    [
        "latent_image",
        "samples",
        "latent",
        "pixels",
        "image",
        "images",
        "video_latent",
        "av_latent"
    ];

    public static IReadOnlyList<WorkflowNode> NodesOfType(JObject workflow, string classType)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (string.IsNullOrWhiteSpace(classType))
        {
            throw new ArgumentException("classType is required.", nameof(classType));
        }

        List<WorkflowNode> nodes = [];
        foreach (JProperty property in workflow.Properties())
        {
            if (property.Value is JObject obj && $"{obj["class_type"]}" == classType)
            {
                nodes.Add(new WorkflowNode(property.Name, obj));
            }
        }

        return nodes;
    }

    public static IReadOnlyList<WorkflowInputConnection> FindInputConnections(JObject workflow, JArray outputRef)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (outputRef is null || outputRef.Count != 2)
        {
            throw new ArgumentException("outputRef must be a [nodeId, outputIndex] pair.", nameof(outputRef));
        }

        string target0 = $"{outputRef[0]}";
        string target1 = $"{outputRef[1]}";
        List<WorkflowInputConnection> matches = [];

        foreach (JProperty nodeProperty in workflow.Properties())
        {
            if (nodeProperty.Value is not JObject node || node["inputs"] is not JObject inputs)
            {
                continue;
            }

            foreach (JProperty input in inputs.Properties())
            {
                if (input.Value is JArray jarr && jarr.Count == 2 && $"{jarr[0]}" == target0 && $"{jarr[1]}" == target1)
                {
                    matches.Add(new WorkflowInputConnection(nodeProperty.Name, input.Name, jarr));
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Finds a <c>VAEDecode</c> node whose <c>inputs.samples</c> points at the given output reference.
    /// This is useful for "final workflow JSON" assertions where you want to distinguish pre-edit vs post-edit decodes.
    /// </summary>
    public static IReadOnlyList<WorkflowNode> FindVaeDecodesBySamples(JObject workflow, JArray samplesRef)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (samplesRef is null || samplesRef.Count != 2)
        {
            throw new ArgumentException("samplesRef must be a [nodeId, outputIndex] pair.", nameof(samplesRef));
        }

        List<WorkflowNode> matches = [];
        foreach (WorkflowNode node in NodesOfType(workflow, "VAEDecode"))
        {
            if (node.Node?["inputs"] is not JObject inputs)
            {
                continue;
            }

            // Most Comfy graphs use "samples"; some variants may rename this to "latent".
            if (inputs.TryGetValue("samples", out JToken samplesTok) && JToken.DeepEquals(samplesTok, samplesRef))
            {
                matches.Add(node);
                continue;
            }

            if (inputs.TryGetValue("latent", out JToken latentTok) && JToken.DeepEquals(latentTok, samplesRef))
            {
                matches.Add(node);
            }
        }

        return matches;
    }

    /// <summary>
    /// Traces upstream from a latent reference and tries to resolve explicit spatial width/height
    /// from prior image/latent resize nodes (including common upscale chains).
    /// </summary>
    public static bool TryResolveSpatialSizeFromLatent(JObject workflow, JArray latentRef, out int width, out int height)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        width = 0;
        height = 0;
        if (latentRef is null || latentRef.Count < 2)
        {
            return false;
        }

        Queue<JArray> pending = new();
        HashSet<string> visitedNodeIds = [];
        pending.Enqueue(latentRef);

        while (pending.Count > 0)
        {
            JArray currentRef = pending.Dequeue();
            string nodeId = $"{currentRef[0]}";
            if (string.IsNullOrWhiteSpace(nodeId) || !visitedNodeIds.Add(nodeId))
            {
                continue;
            }

            if (!workflow.TryGetValue(nodeId, out JToken nodeTok) || nodeTok is not JObject node)
            {
                continue;
            }

            if (TryGetSpatialNodeDimensions(node, out width, out height))
            {
                return true;
            }

            if (node["inputs"] is not JObject inputs)
            {
                continue;
            }

            foreach (string inputKey in SpatialTraceInputKeys)
            {
                if (!inputs.TryGetValue(inputKey, out JToken inputTok))
                {
                    continue;
                }

                foreach (JArray upstreamRef in ExtractNodeRefs(workflow, inputTok))
                {
                    pending.Enqueue(upstreamRef);
                }
            }
        }

        return false;
    }

    private static IEnumerable<JArray> ExtractNodeRefs(JObject workflow, JToken token)
    {
        if (token is not JArray arr)
        {
            yield break;
        }

        if (arr.Count == 2 && arr[0] is not null && arr[1] is JValue idxVal && idxVal.Type == JTokenType.Integer)
        {
            string nodeId = $"{arr[0]}";
            if (!string.IsNullOrWhiteSpace(nodeId) && workflow.ContainsKey(nodeId))
            {
                yield return arr;
                yield break;
            }
        }

        foreach (JToken child in arr)
        {
            foreach (JArray nested in ExtractNodeRefs(workflow, child))
            {
                yield return nested;
            }
        }
    }

    private static bool TryGetSpatialNodeDimensions(JObject node, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (node["inputs"] is not JObject inputs)
        {
            return false;
        }

        string classType = $"{node["class_type"]}";
        bool canDefineDimensions =
            classType == "ImageScale"
            || classType == "SwarmImageScaleForMP"
            || classType.Contains("LatentUpscale", StringComparison.OrdinalIgnoreCase)
            || (classType.Contains("Empty", StringComparison.OrdinalIgnoreCase)
                && classType.Contains("Latent", StringComparison.OrdinalIgnoreCase));
        if (!canDefineDimensions)
        {
            return false;
        }

        return TryGetPositiveInt(inputs["width"], out width)
            && TryGetPositiveInt(inputs["height"], out height);
    }

    private static bool TryGetPositiveInt(JToken token, out int value)
    {
        value = 0;
        if (token is null)
        {
            return false;
        }

        if (token.Type == JTokenType.Integer)
        {
            value = (int)token;
            return value > 0;
        }
        if (token.Type == JTokenType.Float)
        {
            value = (int)Math.Round((double)token);
            return value > 0;
        }
        if (token.Type == JTokenType.String && int.TryParse($"{token}", out int parsed))
        {
            value = parsed;
            return value > 0;
        }

        return false;
    }
}
