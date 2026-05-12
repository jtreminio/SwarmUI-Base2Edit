using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;

namespace Base2Edit.Tests;

public readonly record struct WorkflowNode(string Id, JObject Node);

public readonly record struct WorkflowInputConnection(string NodeId, string InputName, JArray Connection);

/// <summary>
/// Test helpers for querying generated workflow JSON via <see cref="WorkflowBridge"/>.
/// </summary>
public static class WorkflowQuery
{
    public static IReadOnlyList<WorkflowNode> NodesOfType(JObject workflow, string classType)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (string.IsNullOrWhiteSpace(classType))
        {
            throw new ArgumentException("classType is required.", nameof(classType));
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<WorkflowNode> nodes = [];
        foreach (ComfyNode node in bridge.Graph.NodesOfType(classType))
        {
            if (workflow[node.Id] is JObject obj)
            {
                nodes.Add(new WorkflowNode(node.Id, obj));
            }
        }
        return nodes;
    }

    public static IReadOnlyList<WorkflowInputConnection> FindInputConnections(JObject workflow, JArray outputRef)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (outputRef is null || outputRef.Count != 2)
        {
            throw new ArgumentException("outputRef must be a [nodeId, outputIndex] pair.", nameof(outputRef));
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput output = bridge.ResolvePath(outputRef);
        if (output is null)
        {
            return [];
        }

        List<WorkflowInputConnection> matches = [];
        foreach ((ComfyNode node, INodeInput input) in bridge.Graph.FindInputsConnectedTo(output))
        {
            matches.Add(new WorkflowInputConnection(node.Id, input.Name, LiveConnectionJArray(workflow, node.Id, input.Name, outputRef)));
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
        ArgumentNullException.ThrowIfNull(workflow);
        if (fromOutputRef is null || fromOutputRef.Count != 2)
        {
            throw new ArgumentException("fromOutputRef must be a [nodeId, outputIndex] pair.", nameof(fromOutputRef));
        }
        if (toOutputRef is null || toOutputRef.Count != 2)
        {
            throw new ArgumentException("toOutputRef must be a [nodeId, outputIndex] pair.", nameof(toOutputRef));
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput from = bridge.ResolvePath(fromOutputRef);
        INodeOutput to = bridge.ResolvePath(toOutputRef);
        if (from is null || to is null)
        {
            return 0;
        }

        return bridge.Graph.RetargetConnections(from, to, (node, input) =>
        {
            if (shouldRetarget is null)
            {
                return true;
            }
            JArray liveConn = LiveConnectionJArray(workflow, node.Id, input.Name, fromOutputRef);
            return shouldRetarget(new WorkflowInputConnection(node.Id, input.Name, liveConn));
        });
    }

    /// <summary>
    /// Returns true if <paramref name="targetNodeId"/> is reachable by following downstream
    /// connections from <paramref name="startNodeId"/>.
    /// </summary>
    public static bool IsNodeReachableFromNode(JObject workflow, string startNodeId, string targetNodeId)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (string.IsNullOrWhiteSpace(startNodeId) || string.IsNullOrWhiteSpace(targetNodeId))
        {
            return false;
        }
        if (startNodeId == targetNodeId)
        {
            return true;
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode start = bridge.Graph.GetNode(startNodeId);
        if (start is null)
        {
            return false;
        }

        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [startNodeId];
        pending.Enqueue(start);

        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            foreach (INodeOutput output in current.Outputs)
            {
                foreach (ComfyNode consumer in bridge.Graph.FindDownstream(output))
                {
                    if (!visited.Add(consumer.Id))
                    {
                        continue;
                    }
                    if (consumer.Id == targetNodeId)
                    {
                        return true;
                    }
                    pending.Enqueue(consumer);
                }
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
        ArgumentNullException.ThrowIfNull(workflow);
        if (samplesRef is null || samplesRef.Count != 2)
        {
            throw new ArgumentException("samplesRef must be a [nodeId, outputIndex] pair.", nameof(samplesRef));
        }

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        INodeOutput sourceOutput = bridge.ResolvePath(samplesRef);
        if (sourceOutput is null)
        {
            return [];
        }

        List<WorkflowNode> matches = [];
        foreach ((ComfyNode node, INodeInput input) in bridge.Graph.FindInputsConnectedTo(sourceOutput))
        {
            if (node is not VAEDecodeNode)
            {
                continue;
            }
            if (!StringUtils.Equals(input.Name, "samples") && !StringUtils.Equals(input.Name, "latent"))
            {
                continue;
            }
            if (workflow[node.Id] is JObject obj)
            {
                matches.Add(new WorkflowNode(node.Id, obj));
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
        ArgumentNullException.ThrowIfNull(workflow);
        anchorSamples = null;
        anchorImageOut = null;
        anchorVae = null;

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];

        void enqueueProducer(JArray nodeRef)
        {
            if (nodeRef is null || nodeRef.Count != 2)
            {
                return;
            }
            INodeOutput produced = bridge.ResolvePath(nodeRef);
            if (produced?.Node is ComfyNode node && visited.Add(node.Id))
            {
                pending.Enqueue(node);
            }
        }

        enqueueProducer(samplesRef);
        enqueueProducer(imageRef);

        while (pending.Count > 0)
        {
            ComfyNode node = pending.Dequeue();

            if (node is SwarmKSamplerNode or KSamplerAdvancedNode)
            {
                anchorSamples = new JArray(node.Id, 0);
                return true;
            }

            if (node is VAEDecodeNode or VAEDecodeTiledNode)
            {
                anchorImageOut = new JArray(node.Id, 0);
                INodeOutput samplesConn = node.FindInput("samples")?.Connection
                                          ?? node.FindInput("latent")?.Connection;
                if (samplesConn is not null)
                {
                    anchorSamples = new JArray(samplesConn.Node.Id, samplesConn.SlotIndex);
                }
                if (node.FindInput("vae")?.Connection is INodeOutput vaeConn)
                {
                    anchorVae = new JArray(vaeConn.Node.Id, vaeConn.SlotIndex);
                }
                return anchorSamples is not null || anchorImageOut is not null;
            }

            foreach (INodeInput input in node.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream && visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the JArray that <c>Workflow[nodeId]["inputs"][inputName]</c> currently points at,
    /// so callers receiving a <see cref="WorkflowInputConnection"/> can still read (and the rare
    /// in-place mutation case still works) against the live JObject. Falls back to a freshly
    /// constructed pair when the live token is unavailable.
    /// </summary>
    private static JArray LiveConnectionJArray(JObject workflow, string nodeId, string inputName, JArray fallback)
    {
        if (workflow[nodeId] is JObject nodeObj
            && nodeObj["inputs"] is JObject inputs
            && inputs[inputName] is JArray live
            && live.Count == 2)
        {
            return live;
        }
        return new JArray(fallback[0], fallback[1]);
    }
}
