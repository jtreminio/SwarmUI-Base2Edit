using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class WorkflowTests
{
    private static IReadOnlyList<WorkflowNode> NodesOfAnyType(JObject workflow, params string[] classTypes) =>
        (classTypes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => WorkflowUtils.NodesOfType(workflow, t))
            .ToList();

    private static WorkflowNode RequireSingleNodeOfAnyType(JObject workflow, params string[] classTypes)
    {
        IReadOnlyList<WorkflowNode> nodes = NodesOfAnyType(workflow, classTypes);
        Assert.Single(nodes);
        return nodes[0];
    }

    private static JArray RequireConnectionInput(JObject node, params string[] preferredKeys)
    {
        if (node?["inputs"] is not JObject inputs)
        {
            Assert.Fail("Expected node to have an 'inputs' object.");
            return null;
        }

        foreach (string key in preferredKeys ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && inputs.TryGetValue(key, out JToken tok) && tok is JArray arr && arr.Count == 2)
            {
                return arr;
            }
        }

        // Fall back to "first connection-shaped input" for resilience against upstream key renames.
        foreach (JProperty prop in inputs.Properties())
        {
            if (prop.Value is JArray arr && arr.Count == 2)
            {
                return arr;
            }
        }

        Assert.Fail("Expected at least one [nodeId, outputIndex] connection input.");
        return null;
    }

    private static void AssertHasAnyInputConnection(JObject node, JArray expectedRef, string because)
    {
        Assert.NotNull(node);
        Assert.NotNull(expectedRef);
        Assert.True(expectedRef.Count == 2, "expectedRef must be a [nodeId, outputIndex] pair.");

        Assert.True(node["inputs"] is JObject, "Expected node to have an 'inputs' object.");
        JObject inputs = (JObject)node["inputs"];

        bool found = inputs.Properties()
            .Select(p => p.Value)
            .OfType<JArray>()
            .Any(arr => JToken.DeepEquals(arr, expectedRef));

        Assert.True(found, because);
    }

    private static string RequireClassType(JObject workflow, string nodeId)
    {
        Assert.True(workflow.TryGetValue(nodeId, out JToken tok), $"Expected workflow node '{nodeId}' to exist.");
        JObject obj = tok as JObject;
        Assert.NotNull(obj);

        Assert.True(obj.TryGetValue("class_type", out JToken ctTok), $"Expected workflow node '{nodeId}' to have class_type.");
        return $"{ctTok}";
    }

    private static WorkflowNode RequireSingleSampler(JObject workflow) =>
        RequireSingleNodeOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");

    private static T2IParamInput BuildEditInput(string applyAfter)
    {
        _ = WorkflowTestHarness.Base2EditSteps();
        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        if (!string.IsNullOrWhiteSpace(applyAfter))
        {
            input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        }
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        return input;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps()
    {
        return WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());
    }

    [Fact]
    public void ApplyEditAfter_controls_where_edit_stage_is_injected()
    {
        _ = WorkflowTestHarness.Base2EditSteps();

        static WorkflowGenerator.WorkflowGenStep ProbeStep(string probeType, double priority) =>
            new(g =>
            {
                _ = g.CreateNode(probeType, new JObject
                {
                    ["latent"] = g.FinalSamples
                });
            }, priority);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[]
                {
                    WorkflowTestHarness.MinimalGraphSeedStep(),
                    ProbeStep("UnitTest_ProbeAfterBase", 0),
                    ProbeStep("UnitTest_ProbeAfterFinal", 20)
                }
                .Concat(WorkflowTestHarness.Base2EditSteps());

        // Case A: apply after Base -> non-final step runs, final step doesn't.
        {
            T2IParamInput input = BuildEditInput("Base");
            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

            WorkflowNode sampler = RequireSingleNodeOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
            WorkflowNode afterBase = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ProbeAfterBase");
            WorkflowNode afterFinal = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ProbeAfterFinal");

            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput(afterBase.Node, "latent"));
            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput(afterFinal.Node, "latent"));
        }

        // Case B: apply after Refiner -> non-final step doesn't run, final step runs.
        {
            T2IParamInput input = BuildEditInput("Refiner");
            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

            WorkflowNode sampler = RequireSingleNodeOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
            WorkflowNode afterBase = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ProbeAfterBase");
            WorkflowNode afterFinal = WorkflowAssertions.RequireNodeOfType(workflow, "UnitTest_ProbeAfterFinal");

            Assert.Equal(new JArray("10", 0), RequireConnectionInput(afterBase.Node, "latent"));
            Assert.Equal(new JArray(sampler.Id, 0), RequireConnectionInput(afterFinal.Node, "latent"));
        }
    }

    [Fact]
    public void EditStage_runs_after_base_and_leaves_no_final_image()
    {
        T2IParamInput input = BuildEditInput("Base");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));
        var refLatent = WorkflowAssertions.RequireNodeOfType(workflow, "ReferenceLatent");
        WorkflowNode sampler = RequireSingleSampler(workflow);

        // Placement: the ReferenceLatent must read from the pre-edit latent (seed step's FinalSamples),
        // and the edit sampler must consume the ReferenceLatent output.
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(refLatent.Node, "latent"));
        AssertHasAnyInputConnection(sampler.Node, new JArray(refLatent.Id, 0), "Edit sampler must consume ReferenceLatent output (positive conditioning).");
    }

    [Fact]
    public void EditStage_runs_after_refiner_and_outputs_final_image()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.Single(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));
        WorkflowNode refLatent = WorkflowAssertions.RequireNodeOfType(workflow, "ReferenceLatent");
        WorkflowNode sampler = RequireSingleSampler(workflow);

        // Placement: ReferenceLatent must still read the pre-edit latent, and the sampler must consume it.
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(refLatent.Node, "latent"));
        AssertHasAnyInputConnection(sampler.Node, new JArray(refLatent.Id, 0), "Edit sampler must consume ReferenceLatent output (positive conditioning).");

        // Final placement: the VAEDecode should decode the post-edit latent (sampler output).
        WorkflowNode decode = WorkflowAssertions.RequireSingleVaeDecodeBySamples(workflow, new JArray(sampler.Id, 0));
    }

    [Fact]
    public void EditStage_defaults_to_refiner_when_apply_after_missing()
    {
        // If ApplyEditAfter isn't present, Base2Edit should still run on the final step
        // (the param default is "Refiner") as long as an <edit> section exists.
        T2IParamInput input = BuildEditInput(null);
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Assert.NotEmpty(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));
        Assert.NotEmpty(WorkflowUtils.NodesOfType(workflow, "SaveImage"));
    }

    [Fact]
    public void KeepPreEditImage_adds_save_image_node()
    {
        // Use a "final step" run so we can assert SaveImage is wired to the *pre-edit* decode
        // and the generator's FinalImageOut is wired to the *post-edit* decode.
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        IReadOnlyList<WorkflowNode> saves = WorkflowUtils.NodesOfType(workflow, "SaveImage");
        Assert.Single(saves);
        WorkflowNode save = saves[0];
        JArray saveImagesRef = RequireConnectionInput(save.Node, "images");

        // SaveImage.images should come from a VAEDecode node that decodes the pre-edit latent.
        string preEditDecodeId = $"{saveImagesRef[0]}";
        Assert.Equal("VAEDecode", RequireClassType(workflow, preEditDecodeId));

        WorkflowNode preEditDecodeNode = new(preEditDecodeId, (JObject)workflow[preEditDecodeId]);
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(preEditDecodeNode.Node, "samples", "latent"));

        // And there should be a distinct VAEDecode decoding the post-edit latent (sampler output).
        WorkflowNode sampler = RequireSingleSampler(workflow);
        IReadOnlyList<WorkflowNode> postEditDecodes = WorkflowUtils.FindVaeDecodesBySamples(workflow, new JArray(sampler.Id, 0));
        Assert.NotEmpty(postEditDecodes);
        Assert.Contains(postEditDecodes, n => n.Id != preEditDecodeId);
    }

    [Fact]
    public void KeepPreEditImage_with_comfy_saveimage_ws_uses_SwarmSaveImageWS()
    {
        _ = WorkflowTestHarness.Base2EditSteps();

        var input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat([new WorkflowGenerator.WorkflowGenStep(g => g.Features.Add("comfy_saveimage_ws"), -999)])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        IReadOnlyList<WorkflowNode> wsSaves = WorkflowUtils.NodesOfType(workflow, "SwarmSaveImageWS");
        Assert.Single(wsSaves);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "SaveImage"));

        WorkflowNode save = wsSaves[0];
        JArray saveImagesRef = RequireConnectionInput(save.Node, "images");

        // SwarmSaveImageWS.images should come from a VAEDecode node decoding the pre-edit latent.
        string preEditDecodeId = $"{saveImagesRef[0]}";
        Assert.Equal("VAEDecode", RequireClassType(workflow, preEditDecodeId));

        WorkflowNode preEditDecodeNode = new(preEditDecodeId, (JObject)workflow[preEditDecodeId]);
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(preEditDecodeNode.Node, "samples", "latent"));

        // And there should be a distinct post-edit decode from the sampler output.
        WorkflowNode sampler = RequireSingleSampler(workflow);
        IReadOnlyList<WorkflowNode> postEditDecodes = WorkflowUtils.FindVaeDecodesBySamples(workflow, new JArray(sampler.Id, 0));
        Assert.NotEmpty(postEditDecodes);
        Assert.Contains(postEditDecodes, n => n.Id != preEditDecodeId);
    }

    [Fact]
    public void No_pre_edit_flag_means_no_save_node()
    {
        T2IParamInput input = BuildEditInput("Base");

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "SaveImage"));
    }

    [Fact]
    public void Edit_only_image_input_encodes_to_latent_before_edit()
    {
        T2IParamInput input = BuildEditInput("Base");
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_EditOnly()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));

        // Edit-only: when we start with an image and no latents, the edit stage must VAE-encode.
        IReadOnlyList<WorkflowNode> encodes = WorkflowUtils.NodesOfType(workflow, "VAEEncode");
        Assert.Single(encodes);
        WorkflowNode encode = encodes[0];

        WorkflowNode refLatent = WorkflowAssertions.RequireNodeOfType(workflow, "ReferenceLatent");
        Assert.Equal(new JArray(encode.Id, 0), RequireConnectionInput(refLatent.Node, "latent"));
    }

    [Fact]
    public void Image_only_input_final_edit_decodes_output()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_EditOnly()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        WorkflowNode sampler = RequireSingleSampler(workflow);
        WorkflowAssertions.RequireSingleVaeDecodeBySamples(workflow, new JArray(sampler.Id, 0));
    }
}
