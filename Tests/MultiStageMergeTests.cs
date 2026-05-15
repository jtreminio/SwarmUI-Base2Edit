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
        input.Set(T2IParamTypes.Seed, 1);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        return input;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps() =>
        WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());

    private static (T2IModelHandler SdHandler, T2IModelHandler VaeHandler) RegisterSdAndVaeHandlers()
    {
        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["VAE"] = vaeHandler
        };
        return (sdHandler, vaeHandler);
    }

    private static JObject BranchStage(string applyAfter) =>
        new()
        {
            ["applyAfter"]      = applyAfter,
            ["keepPreEditImage"] = false,
            ["refineOnly"]      = true,
            ["control"]         = 0.5,
            ["model"]           = ModelPrep.UseBase,
            ["upscale"]         = 1.25,
            ["upscaleMethod"]   = "pixel-lanczos",
            ["steps"]           = 10,
            ["cfgScale"]        = 1.0
        };

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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        IReadOnlyList<ReferenceLatentNode> refLatents = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refLatents.Count);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        Assert.Empty(WorkflowQuery.FindVaeDecodesBySamples(bridge, bridge.ResolvePath(new JArray("10", 0))));

        Assert.Contains(refLatents, n => n.Latent.Connection?.Node.Id == stage0Sampler.Id && n.Latent.Connection.SlotIndex == 0);
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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);
        KSamplerAdvancedNode ks0 = Assert.IsType<KSamplerAdvancedNode>(stage0Sampler);

        Assert.Equal(4.5, ks0.Cfg.LiteralValue);
        Assert.Equal("dpmpp_2m", ks0.SamplerName.LiteralValue);
        Assert.Equal("karras", ks0.Scheduler.LiteralValue);
    }

    [Fact]
    public void Stage0_uses_default_cfg_sampler_scheduler_when_unset_and_edit_model_is_use_refiner_before_refiner_phase()
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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);
        KSamplerAdvancedNode ks0 = Assert.IsType<KSamplerAdvancedNode>(stage0Sampler);

        Assert.Equal(4.0, ks0.Cfg.LiteralValue);
        Assert.Equal("euler", ks0.SamplerName.LiteralValue);
        Assert.Equal("normal", ks0.Scheduler.LiteralValue);
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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        ComfyNode stage1Sampler = samplers.Single(s => s.Id != stage0Sampler.Id);

        KSamplerAdvancedNode ks0 = Assert.IsType<KSamplerAdvancedNode>(stage0Sampler);
        Assert.Equal(11L, ks0.Steps.LiteralValue);
        Assert.Equal("euler", ks0.SamplerName.LiteralValue);
        Assert.Equal("normal", ks0.Scheduler.LiteralValue);

        KSamplerAdvancedNode ks1 = Assert.IsType<KSamplerAdvancedNode>(stage1Sampler);
        Assert.Equal(33L, ks1.Steps.LiteralValue);
        Assert.Equal("dpmpp_2m", ks1.SamplerName.LiteralValue);
        Assert.Equal("karras", ks1.Scheduler.LiteralValue);

        _ = WorkflowAssertions.RequireSingleVaeDecodeBySamples(bridge, bridge.ResolvePath(new JArray(stage1Sampler.Id, 0)));
    }

    [Fact]
    public void Base_upscale_refiner_with_edit_does_not_leave_decode_on_pre_edit_latent()
    {
        T2IParamInput input = BuildInputWithStage0("Refiner");

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseThenUpscaleThenRefiner()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        ReferenceLatentNode editRefLatent = WorkflowAssertions.RequireNodeOfType<ReferenceLatentNode>(bridge);
        Assert.NotNull(editRefLatent.Latent.Connection);

        IReadOnlyList<VAEDecodeNode> danglingPreEditDecodes = WorkflowQuery.FindVaeDecodesBySamples(bridge, editRefLatent.Latent.Connection);
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
                .Concat([WorkflowTestHarness.PostBaseLatentStep()])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        ReferenceLatentNode stage0Ref = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        _ = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, stage0Ref);

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ReferenceLatentNode>(),
            n => n.Latent.Connection?.Node.Id == "2100" && n.Latent.Connection.SlotIndex == 0
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
                .Concat([WorkflowTestHarness.PostBaseLatentStep()])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ReferenceLatentNode>(),
            n => n.Latent.Connection?.Node.Id == "2100" && n.Latent.Connection.SlotIndex == 0
        );
        Assert.All(
            bridge.Graph.NodesOfType<ReferenceLatentNode>(),
            n => Assert.Equal("10", n.Latent.Connection?.Node.Id)
        );
    }

    [Fact]
    public void Different_models_and_vae_override_can_apply_per_stage()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        (T2IModelHandler sdHandler, T2IModelHandler vaeHandler) = RegisterSdAndVaeHandlers();

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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        ReferenceLatentNode stage1Ref = bridge.Graph.NodesOfType<ReferenceLatentNode>()
            .Single(n => n.Id != ref0.Id);
        ComfyNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, stage1Ref);

        KSamplerAdvancedNode ks0 = Assert.IsType<KSamplerAdvancedNode>(stage0Sampler);
        KSamplerAdvancedNode ks1 = Assert.IsType<KSamplerAdvancedNode>(stage1Sampler);

        Assert.NotNull(ks0.Model.Connection);
        Assert.NotNull(ks1.Model.Connection);
        Assert.NotEqual(ks0.Model.Connection.Node.Id, ks1.Model.Connection.Node.Id);

        VAELoaderNode vaeLoader = bridge.Graph.NodesOfType<VAELoaderNode>().Single();
        Assert.Contains("UnitTest_Vae.safetensors", vaeLoader.VaeName.LiteralValue as string);

        VAEEncodeNode vaeEncode = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Single(n => n.Vae.Connection?.Node.Id == vaeLoader.Id && n.Vae.Connection.SlotIndex == 0);
    }

    [Fact]
    public void Stage0_inherits_refiner_vae_when_unset_and_edit_model_is_use_refiner_before_refiner_phase()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        (T2IModelHandler sdHandler, T2IModelHandler vaeHandler) = RegisterSdAndVaeHandlers();

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;

        T2IModel refinerVae = new(vaeHandler, "/tmp", "/tmp/UnitTest_RefinerVae.safetensors", "UnitTest_RefinerVae.safetensors");
        vaeHandler.Models[refinerVae.Name] = refinerVae;

        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);

        input.Set(T2IParamTypes.RefinerVAE, refinerVae);

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        VAELoaderNode vaeLoader = bridge.Graph.NodesOfType<VAELoaderNode>()
            .Single(n => (n.VaeName.LiteralValue as string)?.Contains("UnitTest_RefinerVae.safetensors") == true);

        VAEEncodeNode vaeEncode = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Single(n => n.Vae.Connection?.Node.Id == vaeLoader.Id && n.Vae.Connection.SlotIndex == 0);
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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        IReadOnlyList<ReferenceLatentNode> refs = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refs.Count);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode sampler0 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        ReferenceLatentNode ref1 = refs.Single(n => n.Id != ref0.Id);
        ComfyNode sampler1 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref1);

        KSamplerAdvancedNode ks0 = Assert.IsType<KSamplerAdvancedNode>(sampler0);
        KSamplerAdvancedNode ks1 = Assert.IsType<KSamplerAdvancedNode>(sampler1);

        Assert.Equal(12.0, ks0.Cfg.LiteralValue);
        Assert.Equal("euler", ks0.SamplerName.LiteralValue);
        Assert.Equal("normal", ks0.Scheduler.LiteralValue);

        Assert.Equal(4.5, ks1.Cfg.LiteralValue);
        Assert.Equal("dpmpp_2m", ks1.SamplerName.LiteralValue);
        Assert.Equal("karras", ks1.Scheduler.LiteralValue);
    }

    [Fact]
    public void Stage1_can_explicitly_set_cfg_sampler_scheduler_and_vae()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        (T2IModelHandler sdHandler, T2IModelHandler vaeHandler) = RegisterSdAndVaeHandlers();
        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        T2IModel stage1Vae = new(vaeHandler, "/tmp", "/tmp/UnitTest_Stage1Vae.safetensors", "UnitTest_Stage1Vae.safetensors");
        vaeHandler.Models[stage1Vae.Name] = stage1Vae;

        T2IParamInput input = BuildInputWithStage0("Base");
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditCFGScale, 12.0);

        JArray stages = new(
            new JObject
            {
                ["applyAfter"] = "Edit Stage 0",
                ["keepPreEditImage"] = false,
                ["control"] = 1.0,
                ["model"] = ModelPrep.UseRefiner,
                ["steps"] = 5,
                ["cfgScale"] = 9.0,
                ["sampler"] = "dpmpp_2m",
                ["scheduler"] = "karras",
                ["vae"] = stage1Vae.Name
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        IReadOnlyList<ReferenceLatentNode> refs = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refs.Count);

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ReferenceLatentNode ref1 = refs.Single(n => n.Id != ref0.Id);
        ComfyNode sampler1 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref1);

        KSamplerAdvancedNode ks1 = Assert.IsType<KSamplerAdvancedNode>(sampler1);
        Assert.Equal(9.0, ks1.Cfg.LiteralValue);
        Assert.Equal("dpmpp_2m", ks1.SamplerName.LiteralValue);
        Assert.Equal("karras", ks1.Scheduler.LiteralValue);

        VAELoaderNode vaeLoader = bridge.Graph.NodesOfType<VAELoaderNode>()
            .Single(n => (n.VaeName.LiteralValue as string)?.Contains("UnitTest_Stage1Vae.safetensors") == true);
        VAEEncodeNode vaeEncode = bridge.Graph.NodesOfType<VAEEncodeNode>()
            .Single(n => n.Vae.Connection?.Node.Id == vaeLoader.Id && n.Vae.Connection.SlotIndex == 0);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, stepsWithRefiner));

        IReadOnlyList<ReferenceLatentNode> refLatents = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.Equal(2, refLatents.Count);

        INodeOutput anchorRef  = refLatents[0].Latent.Connection;
        INodeOutput anchorRef1 = refLatents[1].Latent.Connection;
        Assert.NotNull(anchorRef);
        Assert.NotNull(anchorRef1);
        Assert.Equal(anchorRef.Node.Id, anchorRef1.Node.Id);
        Assert.Equal(anchorRef.SlotIndex, anchorRef1.SlotIndex);

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);

        ComfyNode stage0Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, refLatents[0]);
        ComfyNode stage1Sampler = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, refLatents[1]);

        IReadOnlyList<VAEDecodeNode> decodes = bridge.Graph.NodesOfType<VAEDecodeNode>();
        Assert.True(decodes.Count >= 2, "Expected at least 2 VAEDecode (stage0 final + stage1 branch).");

        IReadOnlyList<SaveImageNode> saveImages = bridge.Graph.NodesOfType<SaveImageNode>();
        Assert.Contains(saveImages, save =>
            save.Images.Connection?.Node is VAEDecodeNode decode
            && decode.Samples.Connection?.Node.Id == stage1Sampler.Id);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, stepsWithRefinerAndWs));

        IReadOnlyList<VAEDecodeNode> vaeDecodes = bridge.Graph.NodesOfType<VAEDecodeNode>();
        Assert.NotEmpty(vaeDecodes);

        HashSet<string> saveNodeIds =
        [
            .. bridge.Graph.NodesOfType<SwarmSaveImageWSNode>().Select(n => n.Id),
            .. bridge.Graph.NodesOfType<SaveImageNode>().Select(n => n.Id)
        ];

        foreach (VAEDecodeNode decode in vaeDecodes)
        {
            INodeOutput imageOut = decode.IMAGE;
            IReadOnlyList<(ComfyNode Node, INodeInput Input)> consumers = WorkflowQuery.FindInputsConnectedTo(bridge, imageOut);
            int saveCount = consumers.Count(c => saveNodeIds.Contains(c.Node.Id));
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

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, BaseSteps()));

        IReadOnlyList<SaveImageNode> saveImages = bridge.Graph.NodesOfType<SaveImageNode>();
        SaveImageNode save0 = Assert.Single(saveImages);

        Assert.IsType<VAEDecodeNode>(save0.Images.Connection?.Node);
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
            BranchStage("Edit Stage 0"),
            BranchStage("Edit Stage 0")
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
        Assert.Equal(3, scales.Count);
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
            BranchStage("Edit Stage 0"),
            BranchStage("Edit Stage 0"),
            BranchStage("Edit Stage 2")
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
    public void Parallel_refiner_branches_do_not_retarget_primary_chain()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();

        (T2IModelHandler sdHandler, T2IModelHandler vaeHandler) = RegisterSdAndVaeHandlers();
        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        T2IModel stage0Vae = new(vaeHandler, "/tmp", "/tmp/UnitTest_Stage0Vae.safetensors", "UnitTest_Stage0Vae.safetensors");
        T2IModel stage1Vae = new(vaeHandler, "/tmp", "/tmp/UnitTest_Stage1Vae.safetensors", "UnitTest_Stage1Vae.safetensors");
        vaeHandler.Models[stage0Vae.Name] = stage0Vae;
        vaeHandler.Models[stage1Vae.Name] = stage1Vae;

        T2IParamInput input = BuildInputWithStage0("Refiner");
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditVAE, stage0Vae);
        input.Set(Base2EditExtension.EditUpscale, 2.0);
        input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
        input.Set(Base2EditExtension.EditSteps, 7);

        JArray stages = new(
            new JObject
            {
                ["applyAfter"]    = "Refiner",
                ["model"]         = ModelPrep.UseBase,
                ["vae"]           = stage1Vae.Name,
                ["upscale"]       = 2.0,
                ["upscaleMethod"] = "pixel-lanczos",
                ["control"]       = 1.0,
                ["steps"]         = 11,
                ["cfgScale"]      = 7.0
            }
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseThenRefiner().Concat(WorkflowTestHarness.Base2EditSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(2, samplers.Count);
        HashSet<string> samplerIds = [.. samplers.Select(s => s.Id)];

        IReadOnlyList<ImageScaleNode> scales = bridge.Graph.NodesOfType<ImageScaleNode>();
        Assert.Equal(2, scales.Count);

        foreach (ImageScaleNode scale in scales)
        {
            VAEDecodeNode decode = Assert.IsType<VAEDecodeNode>(scale.Image.Connection?.Node);
            ComfyNode samplesSource = decode.Samples.Connection?.Node;
            Assert.NotNull(samplesSource);
            Assert.DoesNotContain(samplesSource.Id, samplerIds);
        }
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
            BranchStage("Edit Stage 0"),
            BranchStage("Edit Stage 0"),
            BranchStage("Edit Stage 1")
        );
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(
            input,
            WorkflowTestHarness.Template_BaseThenRefiner().Concat(WorkflowTestHarness.Base2EditSteps())
        ));

        IReadOnlyList<ComfyNode> samplers = WorkflowQuery.Samplers(bridge);
        Assert.Equal(4, samplers.Count);

        IReadOnlyList<SaveImageNode> branchSaves = bridge.Graph.NodesOfType<SaveImageNode>();
        Assert.Single(branchSaves);
    }
}
