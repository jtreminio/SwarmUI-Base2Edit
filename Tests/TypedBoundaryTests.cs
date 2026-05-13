using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class TypedBoundaryTests
{
    public TypedBoundaryTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    private static JObject BuildBase2EditWorkflow()
    {
        return new JObject
        {
            ["4"] = new JObject
            {
                ["class_type"] = "UnitTest_Model",
                ["inputs"] = new JObject()
            },
            ["10"] = new JObject
            {
                ["class_type"] = "UnitTest_Latent",
                ["inputs"] = new JObject()
            },
            ["20"] = new JObject
            {
                ["class_type"] = ReferenceLatentNode.ClassType,
                ["inputs"] = new JObject { ["latent"] = new JArray("10", 0) }
            },
            ["30"] = new JObject
            {
                ["class_type"] = KSamplerAdvancedNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["model"] = new JArray("4", 0),
                    ["positive"] = new JArray("20", 0),
                    ["negative"] = new JArray("20", 0),
                    ["latent_image"] = new JArray("10", 0),
                    ["add_noise"] = "enable",
                    ["noise_seed"] = 42,
                    ["steps"] = 20,
                    ["cfg"] = 7.0,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["start_at_step"] = 0,
                    ["end_at_step"] = 10000,
                    ["return_with_leftover_noise"] = "disable"
                }
            },
            ["40"] = new JObject
            {
                ["class_type"] = VAEDecodeNode.ClassType,
                ["inputs"] = new JObject
                {
                    ["samples"] = new JArray("30", 0),
                    ["vae"] = new JArray("4", 2)
                }
            }
        };
    }

    [Fact]
    public void Bridge_CanQueryTypedReferenceLatentNode()
    {
        JObject workflow = BuildBase2EditWorkflow();
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ReferenceLatentNode> refLatents = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Single(refLatents);
        Assert.Equal("20", refLatents[0].Id);
    }

    [Fact]
    public void ReferenceLatent_LatentConnection_ResolvesToSeedLatent()
    {
        JObject workflow = BuildBase2EditWorkflow();
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode refLatent = bridge.Graph.NodesOfType<ReferenceLatentNode>().Single();
        INodeOutput expectedOutput = bridge.Graph.GetNode("10").Outputs[0];

        Assert.NotNull(refLatent.Latent.Connection);
        Assert.Equal(expectedOutput, refLatent.Latent.Connection);
    }

    [Fact]
    public void Sampler_HasInputConnectedToReferenceLatent()
    {
        JObject workflow = BuildBase2EditWorkflow();
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode refLatent = bridge.Graph.NodesOfType<ReferenceLatentNode>().Single();
        INodeOutput refOutput = refLatent.Outputs[0];
        List<(ComfyNode Node, INodeInput Input)> consumers = [.. bridge.Graph.FindInputsConnectedTo(refOutput)];

        Assert.Contains(consumers, c => c.Node is KSamplerAdvancedNode);
    }

    [Fact]
    public void VAEDecode_Samples_ConnectionToSampler()
    {
        JObject workflow = BuildBase2EditWorkflow();
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAEDecodeNode decode = bridge.Graph.NodesOfType<VAEDecodeNode>().Single();
        KSamplerAdvancedNode sampler = bridge.Graph.NodesOfType<KSamplerAdvancedNode>().Single();

        Assert.NotNull(decode.Samples.Connection);
        Assert.Equal(sampler.Id, decode.Samples.Connection.Node.Id);
    }

    [Fact]
    public void VAEDecode_Vae_ConnectionToModel()
    {
        JObject workflow = BuildBase2EditWorkflow();
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAEDecodeNode decode = bridge.Graph.NodesOfType<VAEDecodeNode>().Single();

        Assert.NotNull(decode.Vae.Connection);
        Assert.Equal("4", decode.Vae.Connection.Node.Id);
    }

    private static (T2IParamInput Input, IEnumerable<WorkflowGenerator.WorkflowGenStep> Steps) BuildHarnessFixture()
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        return (input, steps);
    }

    [Fact]
    public void HarnessGeneratedWorkflow_EditSamplerReachesUpstreamReferenceLatent()
    {
        (T2IParamInput input, IEnumerable<WorkflowGenerator.WorkflowGenStep> steps) = BuildHarnessFixture();

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<ReferenceLatentNode> refLatents = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.NotEmpty(refLatents);
        Assert.NotEmpty(samplers);

        foreach (KSamplerAdvancedNode sampler in samplers)
        {
            bool reachesAnyRefLatent = refLatents.Any(r =>
                TypedWorkflowAssertions.ReachesUpstream(bridge, sampler, r.Id));
            Assert.True(reachesAnyRefLatent, $"Sampler {sampler.Id} should reach some ReferenceLatent");
        }
    }

    [Fact]
    public void MinimalGraphSeedStep_RegistersStubNodes()
    {
        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(
            new T2IParamInput(null),
            [WorkflowTestHarness.MinimalGraphSeedStep()]);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(bridge.Graph.GetNode("4"));
        Assert.NotNull(bridge.Graph.GetNode("10"));
    }

    [Fact]
    public void MinimalGraphSeedStep_AdvancesLastId()
    {
        (JObject _, WorkflowGenerator gen) = WorkflowTestHarness.GenerateWithStepsAndState(
            new T2IParamInput(null),
            [WorkflowTestHarness.MinimalGraphSeedStep()]);

        Assert.True(gen.LastID > 10, "g.LastID should be advanced past stub IDs");
    }
}
