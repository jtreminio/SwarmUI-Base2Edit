using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Base2Edit;

public readonly record struct WorkflowNode(string Id, JObject Node);

public readonly record struct WorkflowInputConnection(string NodeId, string InputName, JArray Connection);

public static class WorkflowUtils
{
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
    /// Rewrites all direct input references from one node output ref to another.
    /// Returns the number of rewritten inputs.
    /// </summary>
    public static int RetargetInputConnections(
        JObject workflow,
        JArray fromOutputRef,
        JArray toOutputRef,
        Func<WorkflowInputConnection, bool> shouldRetarget = null)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (fromOutputRef is null || fromOutputRef.Count != 2)
        {
            throw new ArgumentException("fromOutputRef must be a [nodeId, outputIndex] pair.", nameof(fromOutputRef));
        }
        if (toOutputRef is null || toOutputRef.Count != 2)
        {
            throw new ArgumentException("toOutputRef must be a [nodeId, outputIndex] pair.", nameof(toOutputRef));
        }

        int rewritten = 0;
        foreach (WorkflowInputConnection conn in FindInputConnections(workflow, fromOutputRef))
        {
            if (shouldRetarget is not null && !shouldRetarget(conn))
            {
                continue;
            }

            conn.Connection[0] = toOutputRef[0];
            conn.Connection[1] = toOutputRef[1];
            rewritten++;
        }

        return rewritten;
    }

    /// <summary>
    /// Returns true if <paramref name="targetNodeId"/> is reachable by following downstream
    /// connections from <paramref name="startNodeId"/>.
    /// </summary>
    public static bool IsNodeReachableFromNode(JObject workflow, string startNodeId, string targetNodeId)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (string.IsNullOrWhiteSpace(startNodeId) || string.IsNullOrWhiteSpace(targetNodeId))
        {
            return false;
        }

        if (startNodeId == targetNodeId)
        {
            return true;
        }

        Queue<string> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startNodeId);
        visited.Add(startNodeId);

        while (pending.Count > 0)
        {
            string currentNodeId = pending.Dequeue();
            foreach (JProperty nodeProperty in workflow.Properties())
            {
                if (nodeProperty.Value is not JObject node || node["inputs"] is not JObject inputs)
                {
                    continue;
                }

                bool consumesCurrentNode = false;
                foreach (JProperty input in inputs.Properties())
                {
                    foreach (JArray upstreamRef in ExtractNodeRefs(workflow, input.Value))
                    {
                        if ($"{upstreamRef[0]}" == currentNodeId)
                        {
                            consumesCurrentNode = true;
                            break;
                        }
                    }

                    if (consumesCurrentNode)
                    {
                        break;
                    }
                }

                if (!consumesCurrentNode || !visited.Add(nodeProperty.Name))
                {
                    continue;
                }

                if (nodeProperty.Name == targetNodeId)
                {
                    return true;
                }

                pending.Enqueue(nodeProperty.Name);
            }
        }

        return false;
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
    /// Walks upstream from the current workflow outputs and snaps to the nearest stable edit anchor:
    /// either a sampler output (SwarmKSampler/KSamplerAdvanced) or a VAEDecode image.
    /// This helps keep edit stages attached before downstream post-processing chains.
    /// </summary>
    public static bool TryResolveNearestSamplerOrDecodeAnchor(
        JObject workflow,
        JArray samplesRef,
        JArray imageRef,
        out JArray anchorSamples,
        out JArray anchorImageOut,
        out JArray anchorVae)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        anchorSamples = null;
        anchorImageOut = null;
        anchorVae = null;

        Queue<JArray> pending = new();
        HashSet<string> visitedNodeIds = [];

        static bool IsNodeRef(JArray arr) => arr is not null
            && arr.Count == 2
            && arr[0] is not null
            && arr[1] is JValue idxVal
            && idxVal.Type == JTokenType.Integer;

        static JArray CloneRef(JArray arr) => new(arr[0], arr[1]);

        void enqueueIfNodeRef(JArray nodeRef)
        {
            if (IsNodeRef(nodeRef))
            {
                pending.Enqueue(nodeRef);
            }
        }

        enqueueIfNodeRef(samplesRef);
        enqueueIfNodeRef(imageRef);

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

            string classType = $"{node["class_type"]}";
            if (classType == "SwarmKSampler" || classType == "KSamplerAdvanced")
            {
                anchorSamples = new JArray(nodeId, 0);
                return true;
            }

            if (classType == "VAEDecode" || classType == "VAEDecodeTiled")
            {
                anchorImageOut = new JArray(nodeId, 0);

                if (node["inputs"] is JObject decodeInputs)
                {
                    if (decodeInputs.TryGetValue("samples", out JToken samplesTok) && samplesTok is JArray samplesArr && IsNodeRef(samplesArr))
                    {
                        anchorSamples = CloneRef(samplesArr);
                    }
                    else if (decodeInputs.TryGetValue("latent", out JToken latentTok) && latentTok is JArray latentArr && IsNodeRef(latentArr))
                    {
                        anchorSamples = CloneRef(latentArr);
                    }

                    if (decodeInputs.TryGetValue("vae", out JToken vaeTok) && vaeTok is JArray vaeArr && IsNodeRef(vaeArr))
                    {
                        anchorVae = CloneRef(vaeArr);
                    }
                }

                return anchorSamples is not null || anchorImageOut is not null;
            }

            if (node["inputs"] is not JObject inputs)
            {
                continue;
            }

            foreach (JProperty input in inputs.Properties())
            {
                foreach (JArray upstreamRef in ExtractNodeRefs(workflow, input.Value))
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

}
