using Newtonsoft.Json.Linq;

namespace Base2Edit.Tests;

internal static class WorkflowAssertions
{
    public static WorkflowNode RequireNodeOfType(JObject workflow, string classType)
    {
        foreach (WorkflowNode node in WorkflowUtils.NodesOfType(workflow, classType))
        {
            return node;
        }

        throw new InvalidOperationException($"Expected node with class_type '{classType}' was not found.");
    }

    public static WorkflowNode RequireSingleVaeDecodeBySamples(JObject workflow, JArray samplesRef)
    {
        var matches = WorkflowUtils.FindVaeDecodesBySamples(workflow, samplesRef);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one VAEDecode with samples input {samplesRef}, but found {matches.Count}.");
        }

        return matches[0];
    }
}
