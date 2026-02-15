using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class RefineOnlyTests
{
    private static IReadOnlyList<WorkflowNode> NodesOfAnyType(JObject workflow, params string[] classTypes) =>
        (classTypes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => WorkflowUtils.NodesOfType(workflow, t))
            .ToList();

    private static JArray RequireConnectionInput(JObject node, params string[] preferredKeys)
    {
        Assert.NotNull(node);
        Assert.True(node["inputs"] is JObject, "Expected node to have an 'inputs' object.");
        JObject inputs = (JObject)node["inputs"];

        foreach (string key in preferredKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && inputs.TryGetValue(key, out JToken tok) && tok is JArray arr && arr.Count == 2)
            {
                return arr;
            }
        }

        Assert.Fail("Expected at least one [nodeId, outputIndex] connection input.");
        return null;
    }

    private static string RequireClassType(JObject workflow, string nodeId)
    {
        Assert.True(workflow.TryGetValue(nodeId, out JToken tok), $"Expected workflow node '{nodeId}' to exist.");
        Assert.True(tok is JObject, $"Expected workflow node '{nodeId}' to be an object.");
        JObject obj = (JObject)tok;
        Assert.True(obj.TryGetValue("class_type", out JToken classType), $"Expected workflow node '{nodeId}' to have class_type.");
        return $"{classType}";
    }

    private static T2IParamInput BuildInput()
    {
        _ = WorkflowTestHarness.Base2EditSteps();
        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        return input;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps() =>
        WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());

    [Fact]
    public void RefineOnly_on_stage0_skips_reference_latent()
    {
        T2IParamInput input = BuildInput();
        input.Set(Base2EditExtension.EditRefineOnly, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));

        WorkflowNode sampler = NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler").Single();
        JArray positive = RequireConnectionInput(sampler.Node, "positive");
        Assert.Equal("SwarmClipTextEncodeAdvanced", RequireClassType(workflow, $"{positive[0]}"));
    }

    [Fact]
    public void RefineOnly_applies_per_stage_and_skips_only_target_stage_reference_latent()
    {
        T2IParamInput input = BuildInput();
        var stages = new JArray(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 20,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        // Stage 0 uses ReferenceLatent; stage 1 (refineOnly=true) does not.
        Assert.Single(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));

        WorkflowNode stage0Ref = WorkflowAssertions.RequireReferenceLatentByLatentInput(workflow, new JArray("10", 0));
        WorkflowNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, stage0Ref);
        IReadOnlyList<WorkflowNode> samplers = NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Equal(2, samplers.Count);
        JArray stage0LatentImage = RequireConnectionInput(stage0Sampler.Node, "latent_image", "latent");
        Assert.False(JToken.DeepEquals(stage0LatentImage, new JArray("10", 0)));
        Assert.Contains("Empty", RequireClassType(workflow, $"{stage0LatentImage[0]}"));

        WorkflowNode stage1Sampler = samplers.Single(s =>
            JToken.DeepEquals(RequireConnectionInput(s.Node, "latent_image", "latent"), new JArray(stage0Sampler.Id, 0)));

        JArray stage1Positive = RequireConnectionInput(stage1Sampler.Node, "positive");
        Assert.Equal("SwarmClipTextEncodeAdvanced", RequireClassType(workflow, $"{stage1Positive[0]}"));
    }

    [Fact]
    public void RefineOnly_false_on_later_stage_keeps_reference_latent_even_if_stage0_is_refine_only()
    {
        T2IParamInput input = BuildInput();
        input.Set(Base2EditExtension.EditRefineOnly, true);

        var stages = new JArray(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 20,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        IReadOnlyList<WorkflowNode> samplers = NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Equal(2, samplers.Count);
        Assert.Single(WorkflowUtils.NodesOfType(workflow, "ReferenceLatent"));

        WorkflowNode stage0Sampler = samplers.Single(s =>
            JToken.DeepEquals(RequireConnectionInput(s.Node, "latent_image", "latent"), new JArray("10", 0)));
        JArray stage0Positive = RequireConnectionInput(stage0Sampler.Node, "positive");
        Assert.Equal("SwarmClipTextEncodeAdvanced", RequireClassType(workflow, $"{stage0Positive[0]}"));

        WorkflowNode stage1Ref = WorkflowAssertions.RequireReferenceLatentByLatentInput(workflow, new JArray(stage0Sampler.Id, 0));
        WorkflowNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, stage1Ref);
        Assert.True(JToken.DeepEquals(RequireConnectionInput(stage1Sampler.Node, "positive"), new JArray(stage1Ref.Id, 0)));
    }
}
