using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class RefineOnlyTests
{
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
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());

        ComfyNode sampler = WorkflowQuery.Samplers(bridge).Single();
        JArray positive = RequireConnectionInput((JObject)workflow[sampler.Id], "positive");
        Assert.Equal("SwarmClipTextEncodeAdvanced", RequireClassType(workflow, $"{positive[0]}"));
    }

    [Fact]
    public void RefineOnly_applies_per_stage_and_skips_only_target_stage_reference_latent()
    {
        T2IParamInput input = BuildInput();
        JArray stages = new(
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Stage 0 uses ReferenceLatent; stage 1 (refineOnly=true) does not.
        Assert.Single(bridge.Graph.NodesOfType<ReferenceLatentNode>());

        ReferenceLatentNode stage0Ref = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, stage0Ref);
        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ComfyNode stage1Sampler = samplers.Single(s =>
            JToken.DeepEquals(RequireConnectionInput((JObject)workflow[s.Id], "latent_image", "latent"), new JArray(stage0Sampler.Id, 0)));

        JArray stage1Positive = RequireConnectionInput((JObject)workflow[stage1Sampler.Id], "positive");
        Assert.Equal("SwarmClipTextEncodeAdvanced", RequireClassType(workflow, $"{stage1Positive[0]}"));
    }

    [Fact]
    public void RefineOnly_false_on_later_stage_keeps_reference_latent_even_if_stage0_is_refine_only()
    {
        T2IParamInput input = BuildInput();
        input.Set(Base2EditExtension.EditRefineOnly, true);

        JArray stages = new(
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.Single(bridge.Graph.NodesOfType<ReferenceLatentNode>());

        ComfyNode stage0Sampler = samplers.Single(s =>
            JToken.DeepEquals(RequireConnectionInput((JObject)workflow[s.Id], "latent_image", "latent"), new JArray("10", 0)));
        JArray stage0Positive = RequireConnectionInput((JObject)workflow[stage0Sampler.Id], "positive");
        Assert.Equal("SwarmClipTextEncodeAdvanced", RequireClassType(workflow, $"{stage0Positive[0]}"));

        ReferenceLatentNode stage1Ref = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray(stage0Sampler.Id, 0)));
        ComfyNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, stage1Ref);
        Assert.True(JToken.DeepEquals(RequireConnectionInput((JObject)workflow[stage1Sampler.Id], "positive"), new JArray(stage1Ref.Id, 0)));
    }
}
