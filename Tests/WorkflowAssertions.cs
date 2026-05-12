using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
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
        var matches = WorkflowQuery.FindVaeDecodesBySamples(bridge, samples);
        Assert.Single(matches);
        return matches[0];
    }
}
