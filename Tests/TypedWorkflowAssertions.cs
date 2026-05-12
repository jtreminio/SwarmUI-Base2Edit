using ComfyTyped.Core;

namespace Base2Edit.Tests;

internal static class TypedWorkflowAssertions
{
    /// <summary>
    /// Inclusive variant of <see cref="ComfyGraph.IsReachableUpstream"/>: returns true if
    /// <paramref name="start"/> IS the target node, or if <paramref name="targetNodeId"/>
    /// is reachable by walking upstream from it.
    /// </summary>
    public static bool ReachesUpstream(WorkflowBridge bridge, ComfyNode start, string targetNodeId) =>
        start is not null && (start.Id == targetNodeId || bridge.Graph.IsReachableUpstream(start, targetNodeId));
}
