using ComfyTyped.Core;
using ComfyTyped.Generated;
using Xunit;

namespace Base2Edit.Tests;

internal static class WorkflowAssertions
{
    public static T RequireNodeById<T>(WorkflowBridge bridge, string id) where T : ComfyNode
    {
        ArgumentNullException.ThrowIfNull(bridge);
        T node = bridge.Graph.GetNode<T>(id);
        Assert.NotNull(node);
        return node;
    }

    public static ComfyNode RequireNodeById(WorkflowBridge bridge, string id)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ComfyNode node = bridge.Graph.GetNode(id);
        Assert.NotNull(node);
        return node;
    }

    public static T RequireNodeOfType<T>(WorkflowBridge bridge) where T : ComfyNode
    {
        ArgumentNullException.ThrowIfNull(bridge);
        T node = bridge.Graph.NodesOfType<T>().FirstOrDefault();
        Assert.NotNull(node);
        return node;
    }

    public static ComfyNode RequireNodeOfType(WorkflowBridge bridge, string classType)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ComfyNode node = WorkflowQuery.NodesOfType(bridge, classType).FirstOrDefault();
        Assert.NotNull(node);
        return node;
    }

    public static ReferenceLatentNode RequireReferenceLatentByLatentInput(WorkflowBridge bridge, INodeOutput expectedLatentOutput)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(expectedLatentOutput);

        foreach (ReferenceLatentNode refLatent in bridge.Graph.NodesOfType<ReferenceLatentNode>())
        {
            if (refLatent.Latent.Connection == expectedLatentOutput)
            {
                return refLatent;
            }
        }

        throw new InvalidOperationException($"Expected ReferenceLatent connected to {expectedLatentOutput.Node.Id}:{expectedLatentOutput.SlotIndex} was not found.");
    }

    public static ComfyNode RequireSamplerForReferenceLatent(WorkflowBridge bridge, ReferenceLatentNode refLatent)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(refLatent);

        INodeOutput refOutput = refLatent.Outputs[0];
        foreach ((ComfyNode node, _) in bridge.Graph.FindInputsConnectedTo(refOutput))
        {
            if (node is KSamplerAdvancedNode or SwarmKSamplerNode)
            {
                return node;
            }
        }

        throw new InvalidOperationException($"Expected sampler connected to ReferenceLatent {refLatent.Id} was not found.");
    }

    public static VAEDecodeNode RequireSingleVaeDecodeBySamples(WorkflowBridge bridge, INodeOutput samples)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        IReadOnlyList<VAEDecodeNode> matches = WorkflowQuery.FindVaeDecodesBySamples(bridge, samples);
        Assert.Single(matches);
        return matches[0];
    }
}

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
