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
    private static bool TokenEquals(JToken a, JToken b) => JToken.DeepEquals(a, b);

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
                .Concat([new WorkflowGenerator.WorkflowGenStep(g => g.Features.Add("variation_seed"), -999)])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

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

        JObject samplerInputs = (JObject)workflow[sampler.Id]["inputs"];
        Assert.True(samplerInputs.TryGetValue("model", out JToken samplerModelTok), "Expected sampler.inputs.model");
        JArray samplerModelRef = samplerModelTok as JArray;
        Assert.NotNull(samplerModelRef);

        HashSet<string> loraIds = new(loraLoaders.Select(n => n.Id));
        Assert.Contains($"{samplerModelRef[0]}", loraIds);

        int fromCheckpoint = 0;
        int fromLora = 0;
        foreach (LoraLoaderNode lora in loraLoaders)
        {
            JObject inputsObj = (JObject)workflow[lora.Id]["inputs"];
            Assert.True(inputsObj.TryGetValue("model", out JToken modelTok) && modelTok is JArray, "Expected LoraLoader.inputs.model connection");
            Assert.True(inputsObj.TryGetValue("clip", out JToken clipTok) && clipTok is JArray, "Expected LoraLoader.inputs.clip connection");

            string upstreamModelNode = $"{((JArray)modelTok)[0]}";
            if (upstreamModelNode == loaderId) fromCheckpoint++;
            else if (loraIds.Contains(upstreamModelNode)) fromLora++;
        }
        Assert.Equal(1, fromCheckpoint);
        Assert.Equal(1, fromLora);

        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            JObject inputsObj = (JObject)workflow[enc.Id]["inputs"];
            Assert.True(inputsObj.TryGetValue("clip", out JToken clipTok), "Expected encoder.inputs.clip");
            JArray clipRef = clipTok as JArray;
            Assert.NotNull(clipRef);
            Assert.Contains($"{clipRef[0]}", loraIds);
            Assert.False(TokenEquals(clipRef, new JArray(loaderId, 1)), "Encoder.clip must not come directly from CheckpointLoaderSimple.clip when LoRAs are active");
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        JObject loraInputs = (JObject)workflow[loraId]["inputs"];

        Assert.True(TokenEquals(loraInputs["model"], new JArray(loaderId, 0)), "LoraLoader.model must come from CheckpointLoaderSimple.model");
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(loaderId, 1)), "LoraLoader.clip must come from CheckpointLoaderSimple.clip");

        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            JObject inputsObj = (JObject)workflow[enc.Id]["inputs"];
            Assert.True(TokenEquals(inputsObj["clip"], new JArray(loraId, 1)), "Encoder.clip must come from LoraLoader.clip");
            Assert.False(TokenEquals(inputsObj["clip"], new JArray(loaderId, 1)), "Encoder.clip must not come directly from CheckpointLoaderSimple.clip");
        }

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)workflow[samplers[0].Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)), "Sampler.model must come from LoraLoader.model");
        Assert.False(TokenEquals(samplerInputs["model"], new JArray(loaderId, 0)), "Sampler.model must not come directly from CheckpointLoaderSimple.model");
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
                .Concat([new WorkflowGenerator.WorkflowGenStep(g => g.Features.Add("variation_seed"), -999)])
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

        JObject sampler0Inputs = (JObject)workflow[sampler0.Id]["inputs"];
        JObject sampler1Inputs = (JObject)workflow[sampler1.Id]["inputs"];
        Assert.True(sampler0Inputs.TryGetValue("model", out JToken s0ModelTok) && s0ModelTok is JArray, "Expected stage0 sampler.inputs.model");
        Assert.True(sampler1Inputs.TryGetValue("model", out JToken s1ModelTok) && s1ModelTok is JArray, "Expected stage1 sampler.inputs.model");

        Assert.NotEqual(loraId, $"{((JArray)s0ModelTok)[0]}");
        Assert.Equal(loraId, $"{((JArray)s1ModelTok)[0]}");
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());
        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Empty(loraLoaders);

        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            JObject inputsObj = (JObject)workflow[enc.Id]["inputs"];
            Assert.True(TokenEquals(inputsObj["clip"], new JArray("4", 1)), "Encoder.clip must come from the inherited base-stage clip reference.");
        }

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)workflow[samplers[0].Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray("4", 0)), "Sampler.model must come from the inherited base-stage model reference.");
    }

    [Fact]
    public void Use_base_selection_with_lora_loads_base_model_for_edit_stage_and_applies_edit_lora()
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        JObject loraInputs = (JObject)workflow[loraId]["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray("4", 0)), "LoraLoader.model must come from the inherited base-stage model reference.");
        Assert.True(TokenEquals(loraInputs["clip"], new JArray("4", 1)), "LoraLoader.clip must come from the inherited base-stage clip reference.");

        IReadOnlyList<SwarmClipTextEncodeAdvancedNode> encoders = bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>();
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (SwarmClipTextEncodeAdvancedNode enc in encoders)
        {
            JObject inputsObj = (JObject)workflow[enc.Id]["inputs"];
            Assert.True(TokenEquals(inputsObj["clip"], new JArray(loraId, 1)), "Encoder.clip must come from LoraLoader.clip");
            Assert.False(TokenEquals(inputsObj["clip"], new JArray("4", 1)), "Encoder.clip must not come directly from the inherited base-stage clip reference when LoRAs are active.");
        }

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)workflow[samplers[0].Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)), "Sampler.model must come from LoraLoader.model");
        Assert.False(TokenEquals(samplerInputs["model"], new JArray("4", 0)), "Sampler.model must not come directly from the inherited base-stage model reference when LoRAs are active.");
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)workflow[samplers[0].Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraNodeId, 0)), "Sampler.model must inherit the upstream lora-applied model reference.");
    }

    [Fact]
    public void Use_base_selection_with_edit_section_lora_inherits_stage_loras_and_stacks_edit_lora()
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
        sdHandler.Models[baseModel.Name] = baseModel;

        T2IModel editLora = new(loraHandler, "/tmp", "/tmp/UnitTest_EditLora.safetensors", "UnitTest_EditLora.safetensors");
        loraHandler.Models[editLora.Name] = editLora;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(T2IParamTypes.Prompt, "global <edit>apply extra <lora:UnitTest_EditLora:0.5>");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        const string stageLoraNodeId = "777";
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(
                [
                    new WorkflowGenerator.WorkflowGenStep(g =>
                    {
                        _ = g.CreateNode("UnitTest_BaseLoraAppliedModel", new JObject(), id: stageLoraNodeId, idMandatory: false);
                        g.CurrentModel = new WGNodeData([stageLoraNodeId, 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
                        g.CurrentTextEnc = new WGNodeData([stageLoraNodeId, 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());
                    }, -800)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;

        JObject loraInputs = (JObject)workflow[loraId]["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray(stageLoraNodeId, 0)));
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(stageLoraNodeId, 1)));

        KSamplerAdvancedNode sampler = WorkflowAssertions.RequireNodeOfType<KSamplerAdvancedNode>(bridge);
        JObject samplerInputs = (JObject)workflow[sampler.Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)));
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        JObject loaderInputs = (JObject)workflow[loaders[0].Id]["inputs"];
        Assert.Contains("UnitTest_Edit", $"{loaderInputs["ckpt_name"]}");

        IReadOnlyList<KSamplerAdvancedNode> ks = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        ComfyNode sampler = ks.Count > 0
            ? ks[0]
            : WorkflowAssertions.RequireNodeOfType<SwarmKSamplerNode>(bridge);
        JObject samplerInputs = (JObject)workflow[sampler.Id]["inputs"];

        Assert.Equal(13, (int)samplerInputs["steps"]);
        Assert.Equal(6.5, (double)samplerInputs["cfg"]);
        Assert.Equal("euler", $"{samplerInputs["sampler_name"]}");
        Assert.Equal("normal", $"{samplerInputs["scheduler"]}");
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loaders[0].Id, 0)));
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LoraLoaderNode>());

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);

        KSamplerAdvancedNode sampler = WorkflowAssertions.RequireNodeOfType<KSamplerAdvancedNode>(bridge);
        JObject samplerInputs = (JObject)workflow[sampler.Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loaders[0].Id, 0)));
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;
        JObject loaderInputs = (JObject)workflow[loaderId]["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_Refiner", ckptName);

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        JObject loraInputs = (JObject)workflow[loraId]["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray(loaderId, 0)), "LoraLoader.model must come from CheckpointLoaderSimple.model");
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(loaderId, 1)), "LoraLoader.clip must come from CheckpointLoaderSimple.clip");

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)workflow[samplers[0].Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)), "Sampler.model must come from LoraLoader.model");
        Assert.False(TokenEquals(samplerInputs["model"], new JArray(loaderId, 0)), "Sampler.model must not come directly from CheckpointLoaderSimple.model");
    }

    [Fact]
    public void Use_refiner_selection_with_edit_section_lora_inherits_stage_loras_and_stacks_edit_lora()
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

        T2IModel editLora = new(loraHandler, "/tmp", "/tmp/UnitTest_EditLora.safetensors", "UnitTest_EditLora.safetensors");
        loraHandler.Models[editLora.Name] = editLora;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, refinerModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(T2IParamTypes.Prompt, "global <edit>apply extra <lora:UnitTest_EditLora:0.5>");
        input.Set(Base2EditExtension.ApplyEditAfter, "Refiner");
        input.Set(T2IParamTypes.RefinerMethod, "PostApply");
        input.Set(T2IParamTypes.RefinerControl, 0.2);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        const string stageLoraNodeId = "778";
        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(
                [
                    new WorkflowGenerator.WorkflowGenStep(g =>
                    {
                        g.IsRefinerStage = true;
                        _ = g.CreateNode("UnitTest_RefinerLoraAppliedModel", new JObject(), id: stageLoraNodeId, idMandatory: false);
                        g.CurrentModel = new WGNodeData([stageLoraNodeId, 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
                        g.CurrentTextEnc = new WGNodeData([stageLoraNodeId, 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());
                    }, -800)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;

        JObject loraInputs = (JObject)workflow[loraId]["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray(stageLoraNodeId, 0)));
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(stageLoraNodeId, 1)));

        KSamplerAdvancedNode sampler = WorkflowAssertions.RequireNodeOfType<KSamplerAdvancedNode>(bridge);
        JObject samplerInputs = (JObject)workflow[sampler.Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)));
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;
        JObject loaderInputs = (JObject)workflow[loaderId]["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_Edit", ckptName);

        IReadOnlyList<LoraLoaderNode> loraLoaders = bridge.Graph.NodesOfType<LoraLoaderNode>();
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        JObject loraInputs = (JObject)workflow[loraId]["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray(loaderId, 0)), "LoraLoader.model must come from CheckpointLoaderSimple.model");
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(loaderId, 1)), "LoraLoader.clip must come from CheckpointLoaderSimple.clip");

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)workflow[samplers[0].Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)), "Sampler.model must come from LoraLoader.model");
        Assert.False(TokenEquals(samplerInputs["model"], new JArray(loaderId, 0)), "Sampler.model must not come directly from CheckpointLoaderSimple.model");
    }

    [Fact]
    public void Use_refiner_selection_loads_refiner_model_for_edit_stage()
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
        T2IModel refinerModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Refiner.safetensors", "UnitTest_Refiner.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[refinerModel.Name] = refinerModel;

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, refinerModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(T2IParamTypes.Prompt, "global <edit>no lora here");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);

        JObject loaderInputs = (JObject)workflow[loaders[0].Id]["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_Refiner", ckptName);
    }

    [Fact]
    public void Explicit_edit_model_selection_loads_separate_model_for_edit_stage()
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
        input.Set(Base2EditExtension.EditModel, "UnitTest_Edit.safetensors");
        input.Set(T2IParamTypes.Prompt, "global <edit>edit prompt here");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);

        JObject loaderInputs = (JObject)workflow[loaders[0].Id]["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_Edit", ckptName);

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)workflow[samplers[0].Id]["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loaders[0].Id, 0)), "Sampler.model must come from the edit model loader");
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);

        JObject loaderInputs = (JObject)workflow[loaders[0].Id]["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("Sapphire", ckptName);
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

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<CheckpointLoaderSimpleNode> loaders = bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>();
        Assert.Single(loaders);

        JObject loaderInputs = (JObject)workflow[loaders[0].Id]["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_SD15", ckptName);

        IReadOnlyList<VAEEncodeNode> vaeEncodes = bridge.Graph.NodesOfType<VAEEncodeNode>();
        IReadOnlyList<VAEDecodeNode> vaeDecodes = bridge.Graph.NodesOfType<VAEDecodeNode>();
        Assert.True(vaeEncodes.Count >= 1, "Expected at least one VAEEncode for re-encoding");
        Assert.True(vaeDecodes.Count >= 1, "Expected at least one VAEDecode for pre-edit image");

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Single(samplers);
    }
}
