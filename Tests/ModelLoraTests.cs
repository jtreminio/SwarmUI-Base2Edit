using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class ModelLoraTests
{
    [Fact]
    public void Edit_prompt_multiple_loras_creates_multiple_lora_loaders_and_sampler_uses_last_lora_output()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        T2IModel sdModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
        sdHandler.Models[sdModel.Name] = sdModel;

        T2IModel loraA = new(loraHandler, "/tmp", "/tmp/UnitTest_LoraA.safetensors", "UnitTest_LoraA.safetensors");
        T2IModel loraB = new(loraHandler, "/tmp", "/tmp/UnitTest_LoraB.safetensors", "UnitTest_LoraB.safetensors");
        loraHandler.Models[loraA.Name] = loraA;
        loraHandler.Models[loraB.Name] = loraB;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, sdModel);
        input.Set(T2IParamTypes.Prompt, "global <edit>apply loras <lora:UnitTest_LoraA:0.5> <lora:UnitTest_LoraB:1.0>");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Equal(2, loraLoaders.Count);

        ComfyNode sampler = bridge.Graph.NodesOfType<SwarmKSamplerNode>().FirstOrDefault();
        if (sampler is null)
        {
            sampler = WorkflowAssertions.RequireNodeOfType<KSamplerAdvancedNode>(bridge);
        }

        HashSet<string> loraIds = [.. loraLoaders.Select(n => n.Id)];
        string samplerUpstreamId = (sampler is KSamplerAdvancedNode ks1)
            ? ks1.Model.Connection?.Node.Id
            : (sampler as SwarmKSamplerNode)?.Model.Connection?.Node.Id;
        Assert.NotNull(samplerUpstreamId);
        Assert.Contains(samplerUpstreamId, loraIds);

        int fromCheckpoint = 0;
        int fromLora = 0;
        foreach (LoraLoaderNode lora in loraLoaders)
        {
            Assert.True(lora.Model.IsConnected, "Expected LoraLoader.inputs.model connection");
            Assert.True(lora.Clip.IsConnected, "Expected LoraLoader.inputs.clip connection");

            string upstreamModelNode = lora.Model.Connection?.Node.Id;
            if (upstreamModelNode == loaderId)
            {
                fromCheckpoint++;
            }
            else if (loraIds.Contains(upstreamModelNode))
            {
                fromLora++;
            }
        }
        Assert.Equal(1, fromCheckpoint);
        Assert.Equal(1, fromLora);

        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            string clipUpstreamId = enc.Clip.Connection?.Node.Id;
            Assert.NotNull(clipUpstreamId);
            Assert.Contains(clipUpstreamId, loraIds);
        }
    }

    [Fact]
    public void Edit_prompt_lora_is_wired_between_model_loader_and_downstream_nodes()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        T2IModel sdModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
        sdHandler.Models[sdModel.Name] = sdModel;

        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, sdModel);
        input.Set(T2IParamTypes.Prompt, "global <edit>apply lora <lora:UnitTest_Lora:0.5>");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        LoraLoaderNode loraNode = loraLoaders[0];

        Assert.Equal(loaderId, loraNode.Model.Connection?.Node.Id);
        Assert.Equal(0, loraNode.Model.Connection?.SlotIndex);
        Assert.Equal(loaderId, loraNode.Clip.Connection?.Node.Id);
        Assert.Equal(1, loraNode.Clip.Connection?.SlotIndex);

        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            Assert.Equal(loraId, enc.Clip.Connection?.Node.Id);
            Assert.Equal(1, enc.Clip.Connection?.SlotIndex);
        }

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        Assert.Equal(loraId, samplers[0].Model.Connection?.Node.Id);
        Assert.Equal(0, samplers[0].Model.Connection?.SlotIndex);
    }

    [Fact]
    public void Stage_specific_edit_lora_applies_only_to_target_stage()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        T2IModel sdModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
        sdHandler.Models[sdModel.Name] = sdModel;

        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, sdModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        input.Set(T2IParamTypes.Prompt, "global <edit[0]>stage0 <base>ignore <edit[1]>stage1 <lora:UnitTest_Lora:0.5>");

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

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;

        ReferenceLatentNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(bridge, bridge.ResolvePath(new JArray("10", 0)));
        ComfyNode sampler0 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref0);

        IReadOnlyList<ReferenceLatentNode> refLatents = bridge.Graph.NodesOfType<ReferenceLatentNode>();
        Assert.True(refLatents.Count >= 2, "Expected at least 2 ReferenceLatent nodes for stage0+stage1.");
        ReferenceLatentNode ref1 = refLatents.Single(n =>
            workflow[n.Id] is JObject obj
            && obj["inputs"] is JObject inputs
            && inputs.TryGetValue("latent", out JToken latTok)
            && latTok is JArray arr
            && JToken.DeepEquals(arr, new JArray(sampler0.Id, 0)));
        ComfyNode sampler1 = WorkflowAssertions.RequireSamplerForReferenceLatent(bridge, ref1);

        string s0ModelUpstream = (sampler0 is KSamplerAdvancedNode ks0a)
            ? ks0a.Model.Connection?.Node.Id
            : (sampler0 as SwarmKSamplerNode)?.Model.Connection?.Node.Id;
        string s1ModelUpstream = (sampler1 is KSamplerAdvancedNode ks1a)
            ? ks1a.Model.Connection?.Node.Id
            : (sampler1 as SwarmKSamplerNode)?.Model.Connection?.Node.Id;

        Assert.NotEqual(loraId, s0ModelUpstream);
        Assert.Equal(loraId, s1ModelUpstream);
    }

    [Fact]
    public void Edit_prompt_without_lora_uses_model_directly_without_lora_loader()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        T2IModel sdModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
        sdHandler.Models[sdModel.Name] = sdModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, sdModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(T2IParamTypes.Prompt, "global <edit>no lora here");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());
        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Empty(loraLoaders);

        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            Assert.Equal("4", enc.Clip.Connection?.Node.Id);
            Assert.Equal(1, enc.Clip.Connection?.SlotIndex);
        }

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        Assert.Equal("4", samplers[0].Model.Connection?.Node.Id);
        Assert.Equal(0, samplers[0].Model.Connection?.SlotIndex);
    }

    [Fact]
    public void Use_base_selection_with_lora_loads_base_model_for_edit_stage_and_applies_edit_lora()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        (T2IModelHandler sdHandler, T2IModelHandler loraHandler) = CreateSdAndLoraHandlers();

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;

        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(T2IParamTypes.Prompt, "global <edit>apply lora <lora:UnitTest_Lora:0.5>");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        LoraLoaderNode loraNode = loraLoaders[0];
        Assert.Equal("4", loraNode.Model.Connection?.Node.Id);
        Assert.Equal(0, loraNode.Model.Connection?.SlotIndex);
        Assert.Equal("4", loraNode.Clip.Connection?.Node.Id);
        Assert.Equal(1, loraNode.Clip.Connection?.SlotIndex);

        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            Assert.Equal(loraId, enc.Clip.Connection?.Node.Id);
            Assert.Equal(1, enc.Clip.Connection?.SlotIndex);
        }

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        Assert.Equal(loraId, samplers[0].Model.Connection?.Node.Id);
        Assert.Equal(0, samplers[0].Model.Connection?.SlotIndex);
    }

    [Fact]
    public void Use_base_selection_without_edit_section_inherits_existing_base_model_loras()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(T2IParamTypes.Prompt, "global prompt with no edit section");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        const string loraNodeId = "777";
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(
                [
                    new WorkflowGenerator.WorkflowGenStep(g =>
                    {
                        _ = g.CreateNode("UnitTest_LoraAppliedModel", new JObject(), id: loraNodeId, idMandatory: false);
                        g.CurrentModel = new WGNodeData([loraNodeId, 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
                        g.CurrentTextEnc = new WGNodeData([loraNodeId, 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());
                    }, -800)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        Assert.Equal(loraNodeId, samplers[0].Model.Connection?.Node.Id);
        Assert.Equal(0, samplers[0].Model.Connection?.SlotIndex);
    }

    [Theory]
    [InlineData(ModelPrep.UseBase,    "777", false, "Base")]
    [InlineData(ModelPrep.UseRefiner, "778", true,  "Refiner")]
    public void Edit_section_lora_inherits_stage_loras_and_stacks_edit_lora(
        string editModelMode, string stageLoraNodeId, bool isRefinerStage, string applyEditAfter)
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        (T2IModelHandler sdHandler, T2IModelHandler loraHandler) = CreateSdAndLoraHandlers();

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;

        T2IModel editLora = new(loraHandler, "/tmp", "/tmp/UnitTest_EditLora.safetensors", "UnitTest_EditLora.safetensors");
        loraHandler.Models[editLora.Name] = editLora;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, editModelMode);
        input.Set(T2IParamTypes.Prompt, "global <edit>apply extra <lora:UnitTest_EditLora:0.5>");
        input.Set(Base2EditExtension.ApplyEditAfter, applyEditAfter);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        if (isRefinerStage)
        {
            T2IModel refinerModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Refiner.safetensors", "UnitTest_Refiner.safetensors");
            sdHandler.Models[refinerModel.Name] = refinerModel;
            input.Set(T2IParamTypes.RefinerModel, refinerModel);
            input.Set(T2IParamTypes.RefinerMethod, "PostApply");
            input.Set(T2IParamTypes.RefinerControl, 0.2);
        }

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(
                [
                    new WorkflowGenerator.WorkflowGenStep(g =>
                    {
                        g.IsRefinerStage = isRefinerStage;
                        _ = g.CreateNode("UnitTest_StageLoraAppliedModel", new JObject(), id: stageLoraNodeId, idMandatory: false);
                        g.CurrentModel = new WGNodeData([stageLoraNodeId, 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
                        g.CurrentTextEnc = new WGNodeData([stageLoraNodeId, 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());
                    }, -800)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        LoraLoaderNode loraNode = loraLoaders[0];

        Assert.Equal(stageLoraNodeId, loraNode.Model.Connection?.Node.Id);
        Assert.Equal(0, loraNode.Model.Connection?.SlotIndex);
        Assert.Equal(stageLoraNodeId, loraNode.Clip.Connection?.Node.Id);
        Assert.Equal(1, loraNode.Clip.Connection?.SlotIndex);

        KSamplerAdvancedNode sampler = WorkflowAssertions.RequireNodeOfType<KSamplerAdvancedNode>(bridge);
        Assert.Equal(loraNode.Id, sampler.Model.Connection?.Node.Id);
        Assert.Equal(0, sampler.Model.Connection?.SlotIndex);
    }

    [Fact]
    public void Explicit_edit_model_selection_without_edit_section_loads_selected_model_and_preserves_edit_params()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        T2IModel editModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, editModel.Name);
        input.Set(Base2EditExtension.EditSteps, 13);
        input.Set(Base2EditExtension.EditCFGScale, 6.5);
        input.Set(Base2EditExtension.EditSampler, "euler");
        input.Set(Base2EditExtension.EditScheduler, "normal");
        input.Set(T2IParamTypes.Prompt, "global prompt only");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        Assert.Contains("UnitTest_Edit", $"{loaders[0].CkptName.LiteralValue}");

        IReadOnlyList<KSamplerAdvancedNode> ks = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        if (ks.Count > 0)
        {
            KSamplerAdvancedNode sampler = ks[0];
            Assert.Equal(13L, sampler.Steps.LiteralValue);
            Assert.Equal(6.5, sampler.Cfg.LiteralValue);
            Assert.Equal("euler", sampler.SamplerName.LiteralValue);
            Assert.Equal("normal", sampler.Scheduler.LiteralValue);
            Assert.Equal(loaders[0].Id, sampler.Model.Connection?.Node.Id);
            Assert.Equal(0, sampler.Model.Connection?.SlotIndex);
        }
        else
        {
            SwarmKSamplerNode sampler = WorkflowAssertions.RequireNodeOfType<SwarmKSamplerNode>(bridge);
            Assert.Equal(13L, sampler.Steps.LiteralValue);
            Assert.Equal(6.5, sampler.Cfg.LiteralValue);
            Assert.Equal("euler", sampler.SamplerName.LiteralValue);
            Assert.Equal("normal", sampler.Scheduler.LiteralValue);
            Assert.Equal(loaders[0].Id, sampler.Model.Connection?.Node.Id);
            Assert.Equal(0, sampler.Model.Connection?.SlotIndex);
        }
    }

    [Fact]
    public void Explicit_edit_model_selection_without_edit_section_does_not_inherit_non_global_ui_loras()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        T2IModel editModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        T2IModel lora = new(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[lora.Name] = lora;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, editModel.Name);
        input.Set(T2IParamTypes.Prompt, "global prompt only");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        input.Set(T2IParamTypes.Loras, new List<string> { "UnitTest_Lora" });
        input.Set(T2IParamTypes.LoraWeights, new List<string> { "1" });
        input.Set(T2IParamTypes.LoraSectionConfinement, new List<string> { $"{T2IParamInput.SectionID_BaseOnly}" });

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        Assert.Empty(bridge.Graph.NodesOfType<LoraLoaderNode>());

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
    }

    [Fact]
    public void Use_refiner_selection_with_lora_loads_refiner_model_for_edit_stage_and_applies_edit_lora()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        T2IModel refinerModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Refiner.safetensors", "UnitTest_Refiner.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[refinerModel.Name] = refinerModel;

        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, refinerModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(T2IParamTypes.Prompt, "global <edit>apply lora <lora:UnitTest_Lora:0.5>");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;
        Assert.Contains("UnitTest_Refiner", $"{loaders[0].CkptName.LiteralValue}");

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        LoraLoaderNode loraNode = loraLoaders[0];
        Assert.Equal(loaderId, loraNode.Model.Connection?.Node.Id);
        Assert.Equal(0, loraNode.Model.Connection?.SlotIndex);
        Assert.Equal(loaderId, loraNode.Clip.Connection?.Node.Id);
        Assert.Equal(1, loraNode.Clip.Connection?.SlotIndex);

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        Assert.Equal(loraId, samplers[0].Model.Connection?.Node.Id);
        Assert.Equal(0, samplers[0].Model.Connection?.SlotIndex);
    }

    [Fact]
    public void Explicit_edit_model_selection_with_lora_loads_selected_model_for_edit_stage_and_applies_edit_lora()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        T2IModel editModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, "UnitTest_Edit.safetensors");
        input.Set(T2IParamTypes.Prompt, "global <edit>apply lora <lora:UnitTest_Lora:0.5>");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;
        Assert.Contains("UnitTest_Edit", $"{loaders[0].CkptName.LiteralValue}");

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        LoraLoaderNode loraNode = loraLoaders[0];
        Assert.Equal(loaderId, loraNode.Model.Connection?.Node.Id);
        Assert.Equal(0, loraNode.Model.Connection?.SlotIndex);
        Assert.Equal(loaderId, loraNode.Clip.Connection?.Node.Id);
        Assert.Equal(1, loraNode.Clip.Connection?.SlotIndex);

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        Assert.Equal(loraId, samplers[0].Model.Connection?.Node.Id);
        Assert.Equal(0, samplers[0].Model.Connection?.SlotIndex);
    }

    [Fact]
    public void Edit_model_selection_with_cleaned_name_resolves_correctly()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/base_model.safetensors", "base_model.safetensors");
        T2IModel editModel = new(sdHandler, "/tmp", "/tmp/illustrious/Gem_Collection_-_Sapphire.safetensors", "illustrious/Gem_Collection_-_Sapphire.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        string cleanedEditModelName = T2IParamTypes.CleanModelName(editModel.Name);

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, cleanedEditModelName);
        input.Set(T2IParamTypes.Prompt, "global <edit>edit prompt here");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);

        Assert.Contains("Sapphire", $"{loaders[0].CkptName.LiteralValue}");
    }

    [Fact]
    public void Edit_with_different_compat_class_triggers_reencode()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using SwarmUiTestContext testContext = new();

        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        T2IModelCompatClass sdxlCompat = new() { ID = "sdxl", ShortCode = "SDXL" };
        T2IModelCompatClass sd15Compat = new() { ID = "sd15", ShortCode = "SD15" };

        T2IModelClass sdxlModelClass = new() { ID = "sdxl-base", Name = "SDXL Base", CompatClass = sdxlCompat, StandardWidth = 1024, StandardHeight = 1024 };
        T2IModelClass sd15ModelClass = new() { ID = "sd15-base", Name = "SD 1.5 Base", CompatClass = sd15Compat, StandardWidth = 512, StandardHeight = 512 };

        T2IModel baseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_SDXL.safetensors", "UnitTest_SDXL.safetensors")
        {
            ModelClass = sdxlModelClass
        };
        T2IModel editModel = new(sdHandler, "/tmp", "/tmp/UnitTest_SD15.safetensors", "UnitTest_SD15.safetensors")
        {
            ModelClass = sd15ModelClass
        };
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, "UnitTest_SD15.safetensors");
        input.Set(T2IParamTypes.Prompt, "global <edit>edit prompt here");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        Func<WorkflowGenerator.WorkflowGenStep> harness = () =>
            new WorkflowGenerator.WorkflowGenStep(g =>
            {
                _ = g.CreateNode("UnitTest_Model", new JObject(), id: "4", idMandatory: false);
                _ = g.CreateNode("UnitTest_Latent", new JObject(), id: "10", idMandatory: false);

                g.CurrentModel = new WGNodeData(["4", 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
                g.CurrentTextEnc = new WGNodeData(["4", 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());
                g.CurrentVae = new WGNodeData(["4", 2], g, WGNodeData.DT_VAE, g.CurrentCompat());
                g.CurrentMedia = new WGNodeData(["10", 0], g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());
                g.FinalLoadedModel = baseModel;
                g.FinalLoadedModelList = [baseModel];
            }, -1000);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[] { harness() }
                .Concat(WorkflowTestHarness.Base2EditSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(WorkflowTestHarness.GenerateWithSteps(input, steps));

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);

        Assert.Contains("UnitTest_SD15", $"{loaders[0].CkptName.LiteralValue}");

        IReadOnlyList<VAEEncodeNode> vaeEncodes = bridge.Graph.NodesOfType<VAEEncodeNode>();
        IReadOnlyList<VAEDecodeNode> vaeDecodes = bridge.Graph.NodesOfType<VAEDecodeNode>();
        Assert.True(vaeEncodes.Count >= 1, "Expected at least one VAEEncode for re-encoding");
        Assert.True(vaeDecodes.Count >= 1, "Expected at least one VAEDecode for pre-edit image");

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
    }

    private static (T2IModelHandler sdHandler, T2IModelHandler loraHandler) CreateSdAndLoraHandlers()
    {
        T2IModelHandler sdHandler = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };
        return (sdHandler, loraHandler);
    }
}
