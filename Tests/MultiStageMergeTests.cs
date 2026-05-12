using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class MultiStageMergeTests
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

    private static T2IParamInput BuildInputWithStage0(string applyAfter)
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
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
        T2IParamInput input = BuildInputWithStage0("Base");

        JArray stages = new(
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        IReadOnlyList<ReferenceLatentNode> refLatents = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refLatents.Count);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        Assert.Empty(WorkflowQuery.FindVaeDecodesBySamples(bridge, bridge.ResolvePath(new JArray("10", 0))));

        Assert.Contains(refLatents, n => JToken.DeepEquals(RequireConnectionInput((JObject)workflow[n.Id], "latent"), new JArray(stage0Sampler.Id, 0)));
    }

    [Fact]
    public void Stage0_inherits_base_cfg_sampler_scheduler_when_unset_and_edit_model_is_use_base()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);

        input.Set(T2IParamTypes.CFGScale, 4.5);
        input.Set(ComfyUIBackendExtension.SamplerParam, "dpmpp_2m");
        input.Set(ComfyUIBackendExtension.SchedulerParam, "karras");

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);
        JObject s0Inputs = (JObject)workflow[stage0Sampler.Id]["inputs"];

        Assert.Equal(4.5, (double)s0Inputs["cfg"]);
        Assert.Equal("dpmpp_2m", $"{s0Inputs["sampler_name"]}");
        Assert.Equal("karras", $"{s0Inputs["scheduler"]}");
    }

    [Fact]
    public void Stage0_inherits_refiner_cfg_sampler_scheduler_when_unset_and_edit_model_is_use_refiner_before_refiner_phase()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);

        input.Set(T2IParamTypes.CFGScale, 4.0);
        input.Set(T2IParamTypes.RefinerCFGScale, 9.0);

        input.Set(ComfyUIBackendExtension.SamplerParam, "euler");
        input.Set(ComfyUIBackendExtension.SchedulerParam, "normal");
        input.Set(ComfyUIBackendExtension.RefinerSamplerParam, "dpmpp_2m");
        input.Set(ComfyUIBackendExtension.RefinerSchedulerParam, "karras");

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);
        JObject s0Inputs = (JObject)workflow[stage0Sampler.Id]["inputs"];

        Assert.Equal(9.0, (double)s0Inputs["cfg"]);
        Assert.Equal("dpmpp_2m", $"{s0Inputs["sampler_name"]}");
        Assert.Equal("karras", $"{s0Inputs["scheduler"]}");
    }

    [Fact]
    public void Mixed_apply_after_stages_generate_expected_sampler_settings_and_final_decode()
    {
        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditSteps, 11);
        input.Set(Base2EditExtension.EditSampler, "euler");
        input.Set(Base2EditExtension.EditScheduler, "normal");

        JArray stages = new(
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        ComfyNode stage1Sampler = samplers.Single(s => s.Id != stage0Sampler.Id);

        JObject s0Inputs = (JObject)workflow[stage0Sampler.Id]["inputs"];
        Assert.Equal(11, (int)s0Inputs["steps"]);
        Assert.Equal("euler", $"{s0Inputs["sampler_name"]}");
        Assert.Equal("normal", $"{s0Inputs["scheduler"]}");

        JObject s1Inputs = (JObject)workflow[stage1Sampler.Id]["inputs"];
        Assert.Equal(33, (int)s1Inputs["steps"]);
        Assert.Equal("dpmpp_2m", $"{s1Inputs["sampler_name"]}");
        Assert.Equal("karras", $"{s1Inputs["scheduler"]}");

        _ = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(stage1Sampler.Id, 0)));
    }

    [Fact]
    public void Base_upscale_refiner_with_edit_does_not_leave_decode_on_pre_edit_latent()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseThenUpscaleThenRefiner()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode editRefLatent = WorkflowAssertions.RequireNodeOfType<ReferenceLatentNode>(bridge);
        JArray preEditLatent = RequireConnectionInput((JObject)workflow[editRefLatent.Id], "latent");

        IReadOnlyList<VAEDecodeNode> danglingPreEditDecodes = WorkflowQuery.FindVaeDecodesBySamples(bridge, bridge.ResolvePath(preEditLatent));
        Assert.Empty(danglingPreEditDecodes);

        ComfyNode editSampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, editRefLatent);
        IReadOnlyList<VAEDecodeNode> finalDecodes = WorkflowQuery.FindVaeDecodesBySamples(bridge, bridge.ResolvePath(new JArray(editSampler.Id, 0)));
        Assert.Single(finalDecodes);
    }

    [Fact]
    public void Stage0_apply_after_refiner_falls_back_to_base_when_no_refiner_is_configured()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(
                [
                    new WorkflowGenerator.WorkflowGenStep(g =>
                    {
                        string postBaseLatent = g.CreateNode("UnitTest_PostBaseLatent", [], id: "2100", idMandatory: false);
                        g.CurrentMedia = new WGNodeData([postBaseLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                    }, 2)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode stage0Ref = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        _ = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, stage0Ref);

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ReferenceLatentNode>(),
            n => JToken.DeepEquals(RequireConnectionInput((JObject)workflow[n.Id], "latent"), new JArray("2100", 0))
        );
    }

    [Fact]
    public void Stage1_apply_after_refiner_falls_back_to_base_when_no_refiner_is_configured()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(Base2EditExtension.EditStages, new JArray(
            new JObject
            {
                ["applyAfter"] = "Refiner",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["vae"] = "None",
                ["steps"] = 20,
                ["cfgScale"] = 7.0,
                ["sampler"] = "euler",
                ["scheduler"] = "normal"
            }
        ).ToString());

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(
                [
                    new WorkflowGenerator.WorkflowGenStep(g =>
                    {
                        string postBaseLatent = g.CreateNode("UnitTest_PostBaseLatent", [], id: "2100", idMandatory: false);
                        g.CurrentMedia = new WGNodeData([postBaseLatent, 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                    }, 2)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ReferenceLatentNode>(),
            n => JToken.DeepEquals(RequireConnectionInput((JObject)workflow[n.Id], "latent"), new JArray("2100", 0))
        );
    }

    [Fact]
    public void Different_models_and_vae_override_can_apply_per_stage()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        T2IModel editModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        T2IModel stage1Vae = new(vaeHandler, "/tmp", "/tmp/UnitTest_Vae.safetensors", "UnitTest_Vae.safetensors");
        vaeHandler.Models[stage1Vae.Name] = stage1Vae;

        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditSteps, 10);

        JArray stages = new(
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        ReferenceLatentNode stage1Ref = bridge.Graph.NodesOfType<ReferenceLatentNode>()
            .Single(n => n.Id != ref0.Id);
        ComfyNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, stage1Ref);

        JObject s0Inputs = (JObject)workflow[stage0Sampler.Id]["inputs"];
        JObject s1Inputs = (JObject)workflow[stage1Sampler.Id]["inputs"];

        Assert.True(s0Inputs["model"] is JArray);
        Assert.True(s1Inputs["model"] is JArray);
        Assert.NotEqual($"{((JArray)s0Inputs["model"])[0]}", $"{((JArray)s1Inputs["model"])[0]}");

        VAELoaderNode vaeLoader = bridge.Graph.NodesOfType<VAELoaderNode>().Single();
        Assert.Contains("UnitTest_Vae.safetensors", $"{((JObject)workflow[vaeLoader.Id])["inputs"]["vae_name"]}");

        VAEEncodeNode vaeEncode = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Single(n => JToken.DeepEquals(((JObject)workflow[n.Id])["inputs"]["vae"], new JArray(vaeLoader.Id, 0)));

        Assert.NotNull(vaeEncode);
    }

    [Fact]
    public void Stage0_inherits_refiner_vae_when_unset_and_edit_model_is_use_refiner_before_refiner_phase()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;

        T2IModel refinerVae = new(vaeHandler, "/tmp", "/tmp/UnitTest_RefinerVae.safetensors", "UnitTest_RefinerVae.safetensors");
        vaeHandler.Models[refinerVae.Name] = refinerVae;

        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);

        input.Set(T2IParamTypes.RefinerVAE, refinerVae);

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAELoaderNode vaeLoader = bridge.Graph.NodesOfType<VAELoaderNode>()
            .Single(n => $"{((JObject)workflow[n.Id])["inputs"]["vae_name"]}".Contains("UnitTest_RefinerVae.safetensors"));

        VAEEncodeNode vaeEncode = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Single(n => JToken.DeepEquals(((JObject)workflow[n.Id])["inputs"]["vae"], new JArray(vaeLoader.Id, 0)));

        Assert.NotNull(vaeEncode);
    }

    [Fact]
    public void Stage1_inherits_base_cfg_sampler_scheduler_when_unset_even_if_stage0_set_explicit_values()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditCFGScale, 12.0);
        input.Set(Base2EditExtension.EditSampler, "euler");
        input.Set(Base2EditExtension.EditScheduler, "normal");
        input.Set(T2IParamTypes.CFGScale, 4.5);
        input.Set(ComfyUIBackendExtension.SamplerParam, "dpmpp_2m");
        input.Set(ComfyUIBackendExtension.SchedulerParam, "karras");

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseBase,
                ["steps"] = 5
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ReferenceLatentNode> refs = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refs.Count);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode sampler0 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        ReferenceLatentNode ref1 = refs.Single(n => n.Id != ref0.Id);
        ComfyNode sampler1 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref1);

        JObject s0Inputs = (JObject)workflow[sampler0.Id]["inputs"];
        JObject s1Inputs = (JObject)workflow[sampler1.Id]["inputs"];

        Assert.Equal(12.0, (double)s0Inputs["cfg"]);
        Assert.Equal("euler", $"{s0Inputs["sampler_name"]}");
        Assert.Equal("normal", $"{s0Inputs["scheduler"]}");

        Assert.Equal(4.5, (double)s1Inputs["cfg"]);
        Assert.Equal("dpmpp_2m", $"{s1Inputs["sampler_name"]}");
        Assert.Equal("karras", $"{s1Inputs["scheduler"]}");
    }

    [Fact]
    public void Stage1_inherits_refiner_cfg_sampler_scheduler_and_vae_when_unset_and_stage1_model_is_use_refiner()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };
        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        T2IModel refinerVae = new(vaeHandler, "/tmp", "/tmp/UnitTest_RefinerVae.safetensors", "UnitTest_RefinerVae.safetensors");
        vaeHandler.Models[refinerVae.Name] = refinerVae;

        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditCFGScale, 12.0);
        input.Set(T2IParamTypes.RefinerCFGScale, 9.0);
        input.Set(ComfyUIBackendExtension.RefinerSamplerParam, "dpmpp_2m");
        input.Set(ComfyUIBackendExtension.RefinerSchedulerParam, "karras");
        input.Set(T2IParamTypes.RefinerVAE, refinerVae);

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["steps"] = 5
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ReferenceLatentNode> refs = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refs.Count);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode sampler0 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);
        ReferenceLatentNode ref1 = refs.Single(n => n.Id != ref0.Id);
        ComfyNode sampler1 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref1);

        JObject s1Inputs = (JObject)workflow[sampler1.Id]["inputs"];
        Assert.Equal(9.0, (double)s1Inputs["cfg"]);
        Assert.Equal("dpmpp_2m", $"{s1Inputs["sampler_name"]}");
        Assert.Equal("karras", $"{s1Inputs["scheduler"]}");

        VAELoaderNode vaeLoader = bridge.Graph.NodesOfType<VAELoaderNode>()
            .Single(n => $"{((JObject)workflow[n.Id])["inputs"]["vae_name"]}".Contains("UnitTest_RefinerVae.safetensors"));
        VAEEncodeNode vaeEncode = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Single(n => JToken.DeepEquals(((JObject)workflow[n.Id])["inputs"]["vae"], new JArray(vaeLoader.Id, 0)));
        Assert.NotNull(vaeEncode);
    }

    [Fact]
    public void Two_stages_after_same_anchor_primary_continues_branch_saves_separately()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditSteps, 11);
        input.Set(Base2EditExtension.EditSampler, "euler");
        input.Set(Base2EditExtension.EditScheduler, "normal");

        JArray stages = new(
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ReferenceLatentNode> refLatents = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refLatents.Count);

        JArray anchorRef = RequireConnectionInput((JObject)workflow[refLatents[0].Id], "latent");
        JArray anchorRef1 = RequireConnectionInput((JObject)workflow[refLatents[1].Id], "latent");
        Assert.True(JToken.DeepEquals(anchorRef, anchorRef1), "Both edit stages must read from the same anchor (refiner output).");

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, refLatents[0]);
        ComfyNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, refLatents[1]);

        IReadOnlyList<VAEDecodeNode> decodes = bridge.Graph.NodesOfType<VAEDecodeNode>();
        Assert.True(decodes.Count >= 2, "Expected at least 2 VAEDecode (stage0 final + stage1 branch).");

        string branchSaveId = "51301";
        Assert.True(workflow.ContainsKey(branchSaveId), "Expected SaveImage for branch stage1 output.");
        Assert.Equal("SaveImage", $"{workflow[branchSaveId]!["class_type"]}");
    }

    [Fact]
    public void At_most_one_SwarmSaveImageWS_per_VAEDecode_output()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.KeepPreEditImage, true);
        input.Set(Base2EditExtension.EditSteps, 11);

        JArray stages = new(
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<VAEDecodeNode> vaeDecodes = bridge.Graph.NodesOfType<VAEDecodeNode>();
        Assert.NotEmpty(vaeDecodes);

        foreach (VAEDecodeNode decode in vaeDecodes)
        {
            INodeOutput imageOut = bridge.ResolvePath(new JArray(decode.Id, 0));
            IReadOnlyList<(ComfyNode Node, INodeInput Input)> consumers = WorkflowQuery.FindInputsConnectedTo(bridge, imageOut);
            int saveCount = consumers.Count(c =>
            {
                if (workflow[c.Node.Id] is not JObject node)
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
        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(Base2EditExtension.KeepPreEditImage, true);

        JArray stages = new(
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode save0 = WorkflowAssertions.RequireNodeById(bridge, "51200");
        Assert.Equal("SaveImage", $"{workflow[save0.Id]["class_type"]}");
        Assert.False(workflow.ContainsKey("51201"), "Did not expect a stage1 pre-edit save node.");

        JArray savedImageRef = RequireConnectionInput((JObject)workflow[save0.Id], "images", "image");
        ComfyNode savedImageNode = WorkflowAssertions.RequireNodeById(bridge, $"{savedImageRef[0]}");
        Assert.Equal("VAEDecode", $"{workflow[savedImageNode.Id]["class_type"]}");
    }

    [Fact]
    public void Two_children_after_same_edit_parent_are_both_emitted()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditUpscale, 1.25);
        input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            },
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(
            input,
            WorkflowTestHarness.Template_BaseThenRefiner().Concat(WorkflowTestHarness.Base2EditSteps())
        );
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(3, samplers.Count);

        IReadOnlyList<ImageScaleNode> scales = bridge.Graph.NodesOfType<ImageScaleNode>();
        Assert.True(scales.Count >= 3, "Expected one ImageScale per edit stage.");
    }

    [Fact]
    public void Branch_stage_children_are_ignored()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditUpscale, 1.25);
        input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            },
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            },
            new JObject
            {
                ["applyAfter"] = "Edit Stage 2",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(
            input,
            WorkflowTestHarness.Template_BaseThenRefiner().Concat(WorkflowTestHarness.Base2EditSteps())
        );
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(3, samplers.Count);
    }

    [Fact]
    public void Primary_child_can_chain_while_sibling_branch_stays_leaf()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditUpscale, 1.25);
        input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            },
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            },
            new JObject
            {
                ["applyAfter"] = "Edit Stage 1",
                ["keepPreEditImage"] = false,
                ["refineOnly"] = true,
                ["control"] = 0.5,
                ["model"] = ModelPrep.UseBase,
                ["upscale"] = 1.25,
                ["upscaleMethod"] = "pixel-lanczos",
                ["steps"] = 10,
                ["cfgScale"] = 1.0
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(
            input,
            WorkflowTestHarness.Template_BaseThenRefiner().Concat(WorkflowTestHarness.Base2EditSteps())
        );
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(4, samplers.Count);

        Assert.True(workflow.ContainsKey("51302"), "Expected branch save node for stage2.");
        Assert.False(workflow.ContainsKey("51303"), "Did not expect branch save node for stage3.");
    }
}
