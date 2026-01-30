using Newtonsoft.Json.Linq;
using Xunit;

namespace Base2Edit.Tests;

internal static class WorkflowAssertions
{
    public static WorkflowNode RequireNodeById(JObject workflow, string id)
    {
        Assert.NotNull(workflow);
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.True(workflow.TryGetValue(id, out JToken tok), $"Expected workflow to contain node id '{id}'.");
        Assert.True(tok is JObject, $"Expected workflow node '{id}' to be an object.");
        return new WorkflowNode(id, (JObject)tok);
    }

    public static WorkflowNode RequireNodeOfType(JObject workflow, string classType)
    {
        foreach (WorkflowNode node in WorkflowUtils.NodesOfType(workflow, classType))
        {
            return node;
        }

        throw new InvalidOperationException($"Expected node with class_type '{classType}' was not found.");
    }

    public static WorkflowNode RequireReferenceLatentByLatentInput(JObject workflow, JArray expectedLatentRef)
    {
        Assert.NotNull(workflow);
        Assert.NotNull(expectedLatentRef);
        Assert.Equal(2, expectedLatentRef.Count);

        IReadOnlyList<WorkflowNode> refLatents = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent");
        Assert.NotEmpty(refLatents);

        return refLatents.Single(n => n.Node?["inputs"] is JObject inputs
            && inputs.TryGetValue("latent", out JToken tok)
            && tok is JArray arr
            && JToken.DeepEquals(arr, expectedLatentRef));
    }

    public static WorkflowNode RequireSamplerForReferenceLatent(JObject workflow, WorkflowNode referenceLatent)
    {
        Assert.NotNull(workflow);
        Assert.NotNull(referenceLatent.Node);

        JArray expectedRef = new() { referenceLatent.Id, 0 };
        IReadOnlyList<WorkflowNode> samplers = NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.NotEmpty(samplers);

        return samplers.Single(s => HasAnyInputConnection(s.Node, expectedRef));
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

    private static IReadOnlyList<WorkflowNode> NodesOfAnyType(JObject workflow, params string[] classTypes) =>
        (classTypes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => WorkflowUtils.NodesOfType(workflow, t))
            .ToList();

    private static bool HasAnyInputConnection(JObject node, JArray expectedRef)
    {
        if (node?["inputs"] is not JObject inputs)
        {
            return false;
        }

        return inputs.Properties()
            .Select(p => p.Value)
            .OfType<JArray>()
            .Any(arr => JToken.DeepEquals(arr, expectedRef));
    }
}
