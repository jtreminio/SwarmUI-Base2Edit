using ComfyTyped.Core;
using ComfyTyped.Generated;

namespace Base2Edit.Tests;

/// <summary>
/// Test helpers for querying generated workflow JSON via <see cref="WorkflowBridge"/>.
/// </summary>
public static class WorkflowQuery
{
    public static IReadOnlyList<T> NodesOfType<T>(WorkflowBridge bridge) where T : ComfyNode
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return bridge.Graph.NodesOfType<T>();
    }

    public static IReadOnlyList<ComfyNode> NodesOfType(WorkflowBridge bridge, string classType)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (string.IsNullOrWhiteSpace(classType))
        {
            throw new ArgumentException("classType is required.", nameof(classType));
        }
        return bridge.Graph.NodesOfType(classType);
    }

    /// <summary>
    /// All sampler nodes (<see cref="KSamplerAdvancedNode"/> and <see cref="SwarmKSamplerNode"/>)
    /// in the workflow. Both class types appear in generated workflows depending on backend
    /// path, so callers almost always want them combined.
    /// </summary>
    public static IReadOnlyList<ComfyNode> Samplers(WorkflowBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        List<ComfyNode> samplers = [];
        samplers.AddRange(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        samplers.AddRange(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        return samplers;
    }

    public static IReadOnlyList<(ComfyNode Node, INodeInput Input)> FindInputsConnectedTo(WorkflowBridge bridge, INodeOutput output)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (output is null)
        {
            return [];
        }
        return bridge.Graph.FindInputsConnectedTo(output);
    }

    public static IReadOnlyList<VAEDecodeNode> FindVaeDecodesBySamples(WorkflowBridge bridge, INodeOutput samples)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (samples is null)
        {
            return [];
        }

        List<VAEDecodeNode> matches = [];
        foreach ((ComfyNode node, INodeInput input) in bridge.Graph.FindInputsConnectedTo(samples))
        {
            if (node is not VAEDecodeNode vdNode)
            {
                continue;
            }
            if (!StringUtils.Equals(input.Name, "samples") && !StringUtils.Equals(input.Name, "latent"))
            {
                continue;
            }
            matches.Add(vdNode);
        }
        return matches;
    }
}
