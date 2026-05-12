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
    private static INodeOutput PositiveConnection(ComfyNode sampler) =>
        sampler switch
        {
            KSamplerAdvancedNode ks => ks.Positive.Connection,
            SwarmKSamplerNode sk    => sk.Positive.Connection,
            _                       => null
        };

    private static INodeOutput LatentImageConnection(ComfyNode sampler) =>
        sampler switch
        {
            KSamplerAdvancedNode ks => ks.LatentImage.Connection,
            SwarmKSamplerNode sk    => sk.LatentImage.Connection,
            _                       => null
        };

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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());

        ComfyNode sampler = WorkflowQuery.Samplers(bridge).Single();
        INodeOutput positiveConn = PositiveConnection(sampler);
        Assert.NotNull(positiveConn);
        Assert.IsType<SwarmClipTextEncodeAdvancedNode>(positiveConn.Node);
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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        // Stage 0 uses ReferenceLatent; stage 1 (refineOnly=true) does not.
        Assert.Single(bridge.Graph.NodesOfType<ReferenceLatentNode>());

        ReferenceLatentNode stage0Ref = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, stage0Ref);
        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ComfyNode stage1Sampler = samplers.Single(s =>
        {
            INodeOutput latConn = LatentImageConnection(s);
            return latConn?.Node.Id == stage0Sampler.Id && latConn.SlotIndex == 0;
        });

        INodeOutput stage1PositiveConn = PositiveConnection(stage1Sampler);
        Assert.NotNull(stage1PositiveConn);
        Assert.IsType<SwarmClipTextEncodeAdvancedNode>(stage1PositiveConn.Node);
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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.Single(bridge.Graph.NodesOfType<ReferenceLatentNode>());

        ComfyNode stage0Sampler = samplers.Single(s =>
        {
            INodeOutput latConn = LatentImageConnection(s);
            return latConn?.Node.Id == "10" && latConn.SlotIndex == 0;
        });

        INodeOutput stage0PositiveConn = PositiveConnection(stage0Sampler);
        Assert.NotNull(stage0PositiveConn);
        Assert.IsType<SwarmClipTextEncodeAdvancedNode>(stage0PositiveConn.Node);

        ReferenceLatentNode stage1Ref = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray(stage0Sampler.Id, 0)));
        ComfyNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, stage1Ref);

        INodeOutput stage1PositiveConn = PositiveConnection(stage1Sampler);
        Assert.NotNull(stage1PositiveConn);
        Assert.Equal(stage1Ref.Id, stage1PositiveConn.Node.Id);
        Assert.Equal(0, stage1PositiveConn.SlotIndex);
    }
}
