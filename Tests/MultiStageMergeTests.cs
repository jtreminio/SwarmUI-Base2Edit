using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class MultiStageMergeTests
{
    private static IReadOnlyList<WorkflowNode> NodesOfAnyType(JObject workflow, params string[] classTypes) =>
        (classTypes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => WorkflowUtils.NodesOfType(workflow, t))
            .ToList();

    private static IReadOnlyList<WorkflowNode> Samplers(JObject workflow) =>
        NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");

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

    private static T2IParamInput BuildInputWithStage0(string applyAfter)
    {
        _ = WorkflowTestHarness.Base2EditSteps();
        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner); // enables Base2Edit group
        if (!string.IsNullOrWhiteSpace(applyAfter))
        {
            input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        }
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        return input;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps() =>
        WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());

    [Fact]
    public void Stage0_is_merged_from_root_level_params_and_prepended_to_json_additional_stages()
    {
        // Root-level stage0 says ApplyAfter=Base.
        // Backend should treat stage0 as coming from root-level params, then prepend it before JSON additional stages.
        T2IParamInput input = BuildInputWithStage0("Base");

        // Provide a single additional stage chained after stage0
        var stages = new JArray(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
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

        // Both stages should run in the Base hook (non-final step), so no final image decode should exist.
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));

        IReadOnlyList<WorkflowNode> samplers = Samplers(workflow);
        Assert.Equal(2, samplers.Count);

        IReadOnlyList<WorkflowNode> refLatents = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent");
        Assert.Equal(2, refLatents.Count);

        WorkflowNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(workflow, new JArray("10", 0));
        WorkflowNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, ref0);

        // The other ReferenceLatent (stage 1) must read from stage 0's sampler output.
        Assert.Contains(refLatents, n => JToken.DeepEquals(RequireConnectionInput(n.Node, "latent"), new JArray(stage0Sampler.Id, 0)));
    }

    [Fact]
    public void Mixed_apply_after_stages_generate_expected_sampler_settings_and_final_decode()
    {
        // stage0: Base hook, stage1: Refiner hook
        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(Base2EditExtension.EditSteps, 11);
        input.Set(Base2EditExtension.EditSampler, "euler");
        input.Set(Base2EditExtension.EditScheduler, "normal");

        var stages = new JArray(
            new JObject
            {
                ["applyAfter"] = "Refiner",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 33,
                ["cfgScale"] = 7.0,
                ["sampler"] = "dpmpp_2m",
                ["scheduler"] = "karras"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        IReadOnlyList<WorkflowNode> samplers = Samplers(workflow);
        Assert.Equal(2, samplers.Count);

        // Only the refiner-hook stage should produce a final decode
        Assert.Single(WorkflowUtils.NodesOfType(workflow, "VAEDecode"));

        WorkflowNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(workflow, new JArray("10", 0));
        WorkflowNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, ref0);

        // Stage 1 should consume the stage 0 output latent
        WorkflowNode stage1Ref = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent")
            .Single(n => JToken.DeepEquals(RequireConnectionInput(n.Node, "latent"), new JArray(stage0Sampler.Id, 0)));
        WorkflowNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, stage1Ref);

        JObject s0Inputs = (JObject)stage0Sampler.Node["inputs"];
        Assert.Equal(11, (int)s0Inputs["steps"]);
        Assert.Equal("euler", $"{s0Inputs["sampler_name"]}");
        Assert.Equal("normal", $"{s0Inputs["scheduler"]}");

        JObject s1Inputs = (JObject)stage1Sampler.Node["inputs"];
        Assert.Equal(33, (int)s1Inputs["steps"]);
        Assert.Equal("dpmpp_2m", $"{s1Inputs["sampler_name"]}");
        Assert.Equal("karras", $"{s1Inputs["scheduler"]}");
    }

    [Fact]
    public void Different_models_and_vae_override_can_apply_per_stage()
    {
        using var _ = new SwarmUiTestContext();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var vaeHandler = new T2IModelHandler { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        var editModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        var stage1Vae = new T2IModel(vaeHandler, "/tmp", "/tmp/UnitTest_Vae.safetensors", "UnitTest_Vae.safetensors");
        vaeHandler.Models[stage1Vae.Name] = stage1Vae;

        // stage0 uses base model; stage1 switches model + VAE
        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditSteps, 10);

        var stages = new JArray(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = editModel.Name,
                ["vae"] = stage1Vae.Name,
                ["steps"] = 12,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        WorkflowNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(workflow, new JArray("10", 0));
        WorkflowNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, ref0);

        WorkflowNode stage1Ref = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent")
            .Single(n => n.Id != ref0.Id);
        WorkflowNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, stage1Ref);

        JObject s0Inputs = (JObject)stage0Sampler.Node["inputs"];
        JObject s1Inputs = (JObject)stage1Sampler.Node["inputs"];

        // Different stages can have different models
        Assert.True(s0Inputs["model"] is JArray);
        Assert.True(s1Inputs["model"] is JArray);
        Assert.NotEqual($"{((JArray)s0Inputs["model"])[0]}", $"{((JArray)s1Inputs["model"])[0]}");

        // VAE override should introduce a VAELoader and a corresponding VAEEncode for stage1
        WorkflowNode vaeLoader = WorkflowUtils.NodesOfType(workflow, "VAELoader").Single();
        Assert.Contains("UnitTest_Vae.safetensors", $"{((JObject)vaeLoader.Node["inputs"])["vae_name"]}");

        WorkflowNode vaeEncode = WorkflowUtils.NodesOfType(workflow, "VAEEncode")
            .Single(n => JToken.DeepEquals(((JObject)n.Node["inputs"])["vae"], new JArray(vaeLoader.Id, 0)));

        // The stage1 sampler should take its latent_image from the VAEEncode output
        Assert.True(JToken.DeepEquals(s1Inputs["latent_image"], new JArray(vaeEncode.Id, 0)));
    }

    [Fact]
    public void Two_stages_after_same_anchor_run_in_parallel_save_and_stop()
    {
        // stage0 and stage1 both "after refiner" -> same anchor. Primary (stage0) continues pipeline;
        // parallel (stage1) reads same refiner output, saves its image, and does not feed the pipeline
        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(Base2EditExtension.EditSteps, 11);
        input.Set(Base2EditExtension.EditSampler, "euler");
        input.Set(Base2EditExtension.EditScheduler, "normal");

        var stages = new JArray(
            new JObject
            {
                ["applyAfter"] = "Refiner",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 33,
                ["cfgScale"] = 7.0,
                ["sampler"] = "dpmpp_2m",
                ["scheduler"] = "karras"
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        IEnumerable<WorkflowGenerator.WorkflowGenStep> stepsWithRefiner =
            WorkflowTestHarness.Template_BaseThenRefiner().Concat(WorkflowTestHarness.Base2EditSteps());
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, stepsWithRefiner);

        IReadOnlyList<WorkflowNode> refLatents = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent");
        Assert.Equal(2, refLatents.Count);

        JArray anchorRef = RequireConnectionInput(refLatents[0].Node, "latent");
        JArray anchorRef1 = RequireConnectionInput(refLatents[1].Node, "latent");
        Assert.True(JToken.DeepEquals(anchorRef, anchorRef1), "Both edit stages must read from the same anchor (refiner output).");

        IReadOnlyList<WorkflowNode> samplers = Samplers(workflow);
        Assert.Equal(2, samplers.Count);

        WorkflowNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, refLatents[0]);
        WorkflowNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, refLatents[1]);

        // Only one VAEDecode should feed the main pipeline (stage0); stage1's output is saved separately
        IReadOnlyList<WorkflowNode> decodes = WorkflowUtils.NodesOfType(workflow, "VAEDecode");
        Assert.True(decodes.Count >= 2, "Expected at least 2 VAEDecode (stage0 final + stage1 branch).");

        // Parallel stage1 output is saved via a dedicated SaveImage (id = 1000 + 50300 + 1 = 51301)
        string parallelSaveId = "51301";
        Assert.True(workflow.ContainsKey(parallelSaveId), "Expected SaveImage for parallel stage1 output.");
        Assert.Equal("SaveImage", $"{workflow[parallelSaveId]!["class_type"]}");
    }

    [Fact]
    public void At_most_one_SwarmSaveImageWS_per_VAEDecode_output()
    {
        // Two parallel stages (both after refiner) with KeepPreEditImage: both would save the same
        // pre-edit image (same VAEDecode output). Base2Edit must attach at most one SwarmSaveImageWS per decode
        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);
        input.Set(Base2EditExtension.EditSteps, 11);

        var stages = new JArray(
            new JObject
            {
                ["applyAfter"] = "Refiner",
                ["keepPreEditImage"] = true,
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

        IEnumerable<WorkflowGenerator.WorkflowGenStep> stepsWithRefinerAndWs =
            WorkflowTestHarness.Template_BaseThenRefiner()
                .Concat([new WorkflowGenerator.WorkflowGenStep(g => g.Features.Add("comfy_saveimage_ws"), -999)])
                .Concat(WorkflowTestHarness.Base2EditSteps());
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, stepsWithRefinerAndWs);

        IReadOnlyList<WorkflowNode> vaeDecodes = WorkflowUtils.NodesOfType(workflow, "VAEDecode");
        Assert.NotEmpty(vaeDecodes);

        foreach (WorkflowNode decode in vaeDecodes)
        {
            JArray imageOutRef = new JArray { decode.Id, 0 };
            IReadOnlyList<WorkflowInputConnection> consumers = WorkflowUtils.FindInputConnections(workflow, imageOutRef);
            int saveCount = consumers.Count(c =>
            {
                if (workflow[c.NodeId] is not JObject node)
                {
                    return false;
                }
                string ct = $"{node["class_type"]}";
                return ct == "SwarmSaveImageWS" || ct == "SaveImage";
            });
            Assert.True(saveCount <= 1,
                $"VAEDecode {decode.Id} output is connected to {saveCount} save node(s); expected at most 1.");
        }
    }

    [Fact]
    public void Keep_pre_edit_image_is_respected_per_stage()
    {
        // stage0: keep pre-edit image, stage1: do not keep pre-edit image
        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        var stages = new JArray(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
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

        // stage0 save id is stable: 1000 + 50200 + stageIndex(0) = 51200
        WorkflowNode save0 = WorkflowAssertions.RequireNodeById(workflow, "51200");
        Assert.Equal("SaveImage", $"{save0.Node["class_type"]}");
        Assert.False(workflow.ContainsKey("51201"), "Did not expect a stage1 pre-edit save node.");

        // The save node should point at the pre-edit decode image output
        JArray savedImageRef = RequireConnectionInput(save0.Node, "images", "image");
        WorkflowNode savedImageNode = WorkflowAssertions.RequireNodeById(workflow, $"{savedImageRef[0]}");
        Assert.Equal("VAEDecode", $"{savedImageNode.Node["class_type"]}");
    }
}
