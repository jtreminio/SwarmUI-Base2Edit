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

    public static WorkflowNode RequireNodeOfType(JObject workflow, string classType)
    {
        foreach (WorkflowNode node in NodesOfType(workflow, classType))
        {
            return node;
        }

        throw new InvalidOperationException($"Expected node with class_type '{classType}' was not found.");
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

    public static int ReplaceNodeConnection(JObject workflow, JArray oldRef, JArray newRef)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (oldRef is null || oldRef.Count != 2)
        {
            throw new ArgumentException("oldRef must be a [nodeId, outputIndex] pair.", nameof(oldRef));
        }

        if (newRef is null || newRef.Count != 2)
        {
            throw new ArgumentException("newRef must be a [nodeId, outputIndex] pair.", nameof(newRef));
        }

        string target0 = $"{oldRef[0]}";
        string target1 = $"{oldRef[1]}";
        int replaced = 0;
        foreach (JObject node in workflow.Values<JObject>())
        {
            if (node["inputs"] is not JObject inputs)
            {
                continue;
            }

            foreach (JProperty property in inputs.Properties())
            {
                if (property.Value is JArray jarr && jarr.Count == 2 && $"{jarr[0]}" == target0 && $"{jarr[1]}" == target1)
                {
                    inputs[property.Name] = newRef;
                    replaced++;
                }
            }
        }

        return replaced;
    }

    /// <summary>
    /// Convenience alias for connection rewrites when conceptually "inserting" a stage.
    /// </summary>
    public static int InsertBetween(JObject workflow, JArray upstreamRef, JArray insertedRef) =>
        ReplaceNodeConnection(workflow, upstreamRef, insertedRef);
}
