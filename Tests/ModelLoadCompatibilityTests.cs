using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class ModelLoadCompatibilityTests
{
    private static JObject ModelLoadWorkflow() => new()
    {
        ["4"] = new JObject
        {
            ["class_type"] = CheckpointLoaderSimpleNode.ClassType,
            ["inputs"] = new JObject { ["ckpt_name"] = "test.safetensors" }
        },
        ["11"] = new JObject
        {
            ["class_type"] = SwarmJustLoadTheModelPleaseNode.ClassType,
            ["inputs"] = new JObject
            {
                ["model"] = new JArray("4", 0),
                ["clip"] = new JArray("4", 1),
                ["vae"] = new JArray("4", 2)
            }
        }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Final_edit_imports_skipped_sampler_model_load(bool decodedImage)
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Steps, 0);
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Refiner");

        WorkflowGenerator.WorkflowGenStep skippedSampler = new(g =>
        {
            // Match Swarm's model-only output when base sampling is skipped. The
            // checkpoint must be typed: UnknownNode outputs conceal this mismatch.
            foreach (JProperty node in ModelLoadWorkflow().Properties())
            {
                g.Workflow[node.Name] = node.Value;
            }
            g.IsRefinerStage = true;
            if (decodedImage)
            {
                g.CurrentMedia = g.CurrentMedia.AsRawImage(g.CurrentVae);
            }
        }, 0);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Append(skippedSampler)
                .Concat(WorkflowTestHarness.Base2EditSteps());
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(WorkflowQuery.Samplers(bridge));
        Assert.Equal("CLIP", bridge.Graph.GetNode("11").FindInput("clip").Connection.TypeName);
        Assert.True(JToken.DeepEquals(ModelLoadWorkflow()["11"], workflow["11"]));
    }
}
