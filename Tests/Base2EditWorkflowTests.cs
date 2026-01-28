using System.Collections.Generic;
using System.Linq;
using Base2Edit;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class Base2EditWorkflowTests
{
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
        yield return WorkflowTestHarness.MinimalGraphSeedStep();
        foreach (WorkflowGenerator.WorkflowGenStep step in WorkflowTestHarness.Base2EditSteps())
        {
            yield return step;
        }
    }

    private static WorkflowGenerator.WorkflowGenStep ImageOnlySeedStep() =>
        new(g =>
        {
            string imageNode = g.CreateNode("UnitTest_Image", new JObject(), id: "11", idMandatory: false);
            g.FinalImageOut = [imageNode, 0];
            // In edit-only flows, we have an input image but no latent samples yet.
            // This forces the edit stage to VAE-encode before sampling.
            g.FinalSamples = null;
        }, -900);

    [Fact]
    public void EditStage_runs_after_base_and_leaves_no_final_image()
    {
        T2IParamInput input = BuildEditInput("Base");
        (JObject workflow, WorkflowGenerator gen) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BaseSteps());

        Assert.Null(gen.FinalImageOut);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));
        var refLatent = WorkflowUtils.RequireNodeOfType(workflow, "ReferenceLatent");
        var sampler = WorkflowUtils.RequireNodeOfType(workflow, "KSamplerAdvanced");

        // Placement: the ReferenceLatent must read from the pre-edit latent (seed step's FinalSamples),
        // and the edit sampler must consume the ReferenceLatent output.
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(refLatent.Node, "latent"));
        AssertHasAnyInputConnection(sampler.Node, new JArray(refLatent.Id, 0), "Edit sampler must consume ReferenceLatent output (positive conditioning).");
    }

    [Fact]
    public void EditStage_runs_after_refiner_and_outputs_final_image()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        (JObject workflow, WorkflowGenerator gen) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BaseSteps());

        Assert.NotNull(gen.FinalImageOut);
        Assert.Single(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));
        WorkflowNode refLatent = WorkflowUtils.RequireNodeOfType(workflow, "ReferenceLatent");
        WorkflowNode sampler = WorkflowUtils.RequireNodeOfType(workflow, "KSamplerAdvanced");

        // Placement: ReferenceLatent must still read the pre-edit latent, and the sampler must consume it.
        Assert.Equal(new JArray("10", 0), RequireConnectionInput(refLatent.Node, "latent"));
        AssertHasAnyInputConnection(sampler.Node, new JArray(refLatent.Id, 0), "Edit sampler must consume ReferenceLatent output (positive conditioning).");

        // Final placement: FinalImageOut should be the output of a VAEDecode node (post-edit decode).
        Assert.True(gen.FinalImageOut is JArray, "Expected generator FinalImageOut to be a [nodeId, outputIndex] pair.");
        JArray finalOut = (JArray)gen.FinalImageOut;
        Assert.Equal(2, finalOut.Count);
        string finalNodeId = $"{finalOut[0]}";
        Assert.Equal("VAEDecode", RequireClassType(workflow, finalNodeId));
    }

    [Fact]
    public void EditStage_defaults_to_refiner_when_apply_after_missing()
    {
        // If ApplyEditAfter isn't present, Base2Edit should still run on the final step
        // (the param default is "Refiner") as long as an <edit> section exists.
        T2IParamInput input = BuildEditInput(null);
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        (JObject workflow, WorkflowGenerator gen) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BaseSteps());

        Assert.NotNull(gen.FinalImageOut);
        Assert.NotEmpty(WorkflowUtils.NodesOfType(workflow, "SaveImage"));
    }

    [Fact]
    public void KeepPreEditImage_adds_save_image_node()
    {
        // Use a "final step" run so we can assert SaveImage is wired to the *pre-edit* decode
        // and the generator's FinalImageOut is wired to the *post-edit* decode.
        T2IParamInput input = BuildEditInput("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        (JObject workflow, WorkflowGenerator gen) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BaseSteps());

        IReadOnlyList<WorkflowNode> saves = WorkflowUtils.NodesOfType(workflow, "SaveImage");
        Assert.Single(saves);
        WorkflowNode save = saves[0];
        JArray saveImagesRef = RequireConnectionInput(save.Node, "images");

        // SaveImage must not be wired to the final output image (it should capture the pre-edit image).
        Assert.True(gen.FinalImageOut is JArray, "Expected generator FinalImageOut to be a [nodeId, outputIndex] pair.");
        JArray finalOut = (JArray)gen.FinalImageOut;
        Assert.Equal(2, finalOut.Count);
        Assert.False(JToken.DeepEquals(saveImagesRef, finalOut), "SaveImage.images must not point at the final (post-edit) image output.");

        // SaveImage.images should come from a VAEDecode node that decodes the pre-edit latent.
        string preEditDecodeId = $"{saveImagesRef[0]}";
        Assert.Equal("VAEDecode", RequireClassType(workflow, preEditDecodeId));

        WorkflowNode preEditDecodeNode = new(preEditDecodeId, (JObject)workflow[preEditDecodeId]);
        AssertHasAnyInputConnection(preEditDecodeNode.Node, new JArray("10", 0), "Pre-edit VAEDecode should decode the pre-edit latent samples.");
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
            new[] { WorkflowTestHarness.MinimalGraphSeedStep(), ImageOnlySeedStep() }
                .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator gen) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, steps);

        Assert.NotNull(gen.FinalSamples);
        Assert.Null(gen.FinalImageOut);

        // Edit-only: when we start with an image and no latents, the edit stage must VAE-encode.
        IReadOnlyList<WorkflowNode> encodes = WorkflowUtils.NodesOfType(workflow, "VAEEncode");
        Assert.Single(encodes);
        WorkflowNode encode = encodes[0];

        WorkflowNode refLatent = WorkflowUtils.RequireNodeOfType(workflow, "ReferenceLatent");
        Assert.Equal(new JArray(encode.Id, 0), RequireConnectionInput(refLatent.Node, "latent"));
    }

    [Fact]
    public void Image_only_input_final_edit_decodes_output()
    {
        T2IParamInput input = BuildEditInput("Refiner");
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[] { WorkflowTestHarness.MinimalGraphSeedStep(), ImageOnlySeedStep() }
                .Concat(WorkflowTestHarness.Base2EditSteps());

        (JObject workflow, WorkflowGenerator gen) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, steps);

        Assert.NotNull(gen.FinalSamples);
        Assert.NotNull(gen.FinalImageOut);
        Assert.Single(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));

        // Final output should be the post-edit VAEDecode output.
        Assert.True(gen.FinalImageOut is JArray, "Expected generator FinalImageOut to be a [nodeId, outputIndex] pair.");
        JArray finalOut = (JArray)gen.FinalImageOut;
        Assert.Equal(2, finalOut.Count);
        string finalNodeId = $"{finalOut[0]}";
        Assert.Equal("VAEDecode", RequireClassType(workflow, finalNodeId));
    }
}
