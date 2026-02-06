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
}
