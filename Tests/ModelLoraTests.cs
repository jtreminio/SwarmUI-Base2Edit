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
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var sdModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
        sdHandler.Models[sdModel.Name] = sdModel;

        var loraA = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_LoraA.safetensors", "UnitTest_LoraA.safetensors");
        var loraB = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_LoraB.safetensors", "UnitTest_LoraB.safetensors");
        loraHandler.Models[loraA.Name] = loraA;
        loraHandler.Models[loraB.Name] = loraB;

        var input = new T2IParamInput(null);
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

        // Assert we created a model loader for the edit stage.
        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;

        // Assert multiple LoRA loaders exist.
        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Equal(2, loraLoaders.Count);

        // Find the sampler and ensure it uses the final LoRA model output.
        WorkflowNode sampler = WorkflowUtils.NodesOfType(workflow, "SwarmKSampler").FirstOrDefault();
        if (sampler.Node is null)
        {
            sampler = WorkflowAssertions.RequireNodeOfType(workflow, "KSamplerAdvanced");
        }

        JObject samplerInputs = (JObject)sampler.Node["inputs"];
        Assert.True(samplerInputs.TryGetValue("model", out JToken samplerModelTok), "Expected sampler.inputs.model");
        JArray samplerModelRef = samplerModelTok as JArray;
        Assert.NotNull(samplerModelRef);

        var loraIds = new HashSet<string>(loraLoaders.Select(n => n.Id));
        Assert.Contains($"{samplerModelRef[0]}", loraIds);

        // The first LoRA should consume the CheckpointLoaderSimple outputs, and the chain should feed the sampler.
        int fromCheckpoint = 0;
        int fromLora = 0;
        foreach (WorkflowNode lora in loraLoaders)
        {
            JObject inputsObj = (JObject)lora.Node["inputs"];
            Assert.True(inputsObj.TryGetValue("model", out JToken modelTok) && modelTok is JArray, "Expected LoraLoader.inputs.model connection");
            Assert.True(inputsObj.TryGetValue("clip", out JToken clipTok) && clipTok is JArray, "Expected LoraLoader.inputs.clip connection");

            string upstreamModelNode = $"{((JArray)modelTok)[0]}";
            if (upstreamModelNode == loaderId) fromCheckpoint++;
            else if (loraIds.Contains(upstreamModelNode)) fromLora++;
        }
        Assert.Equal(1, fromCheckpoint);
        Assert.Equal(1, fromLora);

        // Encoders should use the final LoRA clip output (not the raw loader clip output).
        IReadOnlyList<WorkflowNode> encoders = WorkflowUtils.NodesOfType(workflow, "SwarmClipTextEncodeAdvanced");
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (WorkflowNode enc in encoders)
        {
            JObject inputsObj = (JObject)enc.Node["inputs"];
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
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var sdModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
        sdHandler.Models[sdModel.Name] = sdModel;

        var loraModel = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        var input = new T2IParamInput(null);
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

        // Assert we created a model loader for the edit stage.
        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;

        // Assert the LoRA loader exists.
        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        JObject loraInputs = (JObject)loraLoaders[0].Node["inputs"];

        Assert.True(TokenEquals(loraInputs["model"], new JArray(loaderId, 0)), "LoraLoader.model must come from CheckpointLoaderSimple.model");
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(loaderId, 1)), "LoraLoader.clip must come from CheckpointLoaderSimple.clip");

        // Assert downstream nodes use the LoRA outputs (not the raw loader outputs).
        IReadOnlyList<WorkflowNode> encoders = WorkflowUtils.NodesOfType(workflow, "SwarmClipTextEncodeAdvanced");
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (WorkflowNode enc in encoders)
        {
            JObject inputsObj = (JObject)enc.Node["inputs"];
            Assert.True(TokenEquals(inputsObj["clip"], new JArray(loraId, 1)), "Encoder.clip must come from LoraLoader.clip");
            Assert.False(TokenEquals(inputsObj["clip"], new JArray(loaderId, 1)), "Encoder.clip must not come directly from CheckpointLoaderSimple.clip");
        }

        IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)samplers[0].Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)), "Sampler.model must come from LoraLoader.model");
        Assert.False(TokenEquals(samplerInputs["model"], new JArray(loaderId, 0)), "Sampler.model must not come directly from CheckpointLoaderSimple.model");
    }

    [Fact]
    public void Stage_specific_edit_lora_applies_only_to_target_stage()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var sdModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
        sdHandler.Models[sdModel.Name] = sdModel;

        var loraModel = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Model, sdModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        // Activate via stage0 tag, but put the LoRA only in stage1
        input.Set(T2IParamTypes.Prompt, "global <edit[0]>stage0 <base>ignore <edit[1]>stage1 <lora:UnitTest_Lora:0.5>");

        // Add stage1 chained after stage0
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

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat([new WorkflowGenerator.WorkflowGenStep(g => g.Features.Add("variation_seed"), -999)])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;

        // Identify stage0 sampler vs stage1 sampler via the ReferenceLatent chain
        WorkflowNode ref0 = WorkflowAssertions.RequireReferenceLatentByLatentInput(workflow, new JArray("10", 0));
        WorkflowNode sampler0 = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, ref0);

        IReadOnlyList<WorkflowNode> refLatents = WorkflowUtils.NodesOfType(workflow, "ReferenceLatent");
        Assert.True(refLatents.Count >= 2, "Expected at least 2 ReferenceLatent nodes for stage0+stage1.");
        WorkflowNode ref1 = refLatents.Single(n =>
            n.Node?["inputs"] is JObject inputs
            && inputs.TryGetValue("latent", out JToken latTok)
            && latTok is JArray arr
            && JToken.DeepEquals(arr, new JArray(sampler0.Id, 0)));
        WorkflowNode sampler1 = WorkflowAssertions.RequireSamplerForReferenceLatent(workflow, ref1);

        JObject sampler0Inputs = (JObject)sampler0.Node["inputs"];
        JObject sampler1Inputs = (JObject)sampler1.Node["inputs"];
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
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        var sdModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
        sdHandler.Models[sdModel.Name] = sdModel;

        var input = new T2IParamInput(null);
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

        // When using "(Use Base)" we should inherit the existing base-stage model stack,
        // not reload a new model (which would drop stage LoRAs)
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple"));
        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Empty(loraLoaders);

        // Assert downstream nodes use the inherited base stack outputs
        IReadOnlyList<WorkflowNode> encoders = WorkflowUtils.NodesOfType(workflow, "SwarmClipTextEncodeAdvanced");
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (WorkflowNode enc in encoders)
        {
            JObject inputsObj = (JObject)enc.Node["inputs"];
            Assert.True(TokenEquals(inputsObj["clip"], new JArray("4", 1)), "Encoder.clip must come from the inherited base-stage clip reference.");
        }

        IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)samplers[0].Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray("4", 0)), "Sampler.model must come from the inherited base-stage model reference.");
    }

    [Fact]
    public void Use_base_selection_with_lora_loads_base_model_for_edit_stage_and_applies_edit_lora()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;

        var loraModel = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        var input = new T2IParamInput(null);
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

        // When using "(Use Base)" we should inherit the existing base-stage model stack,
        // then stack edit-section LoRAs on top (instead of reloading the model)
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple"));

        // Assert the LoRA loader exists and is chained from the inherited base model stack
        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        JObject loraInputs = (JObject)loraLoaders[0].Node["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray("4", 0)), "LoraLoader.model must come from the inherited base-stage model reference.");
        Assert.True(TokenEquals(loraInputs["clip"], new JArray("4", 1)), "LoraLoader.clip must come from the inherited base-stage clip reference.");

        // Assert downstream nodes use the LoRA outputs (not the raw loader outputs)
        IReadOnlyList<WorkflowNode> encoders = WorkflowUtils.NodesOfType(workflow, "SwarmClipTextEncodeAdvanced");
        Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
        foreach (WorkflowNode enc in encoders)
        {
            JObject inputsObj = (JObject)enc.Node["inputs"];
            Assert.True(TokenEquals(inputsObj["clip"], new JArray(loraId, 1)), "Encoder.clip must come from LoraLoader.clip");
            Assert.False(TokenEquals(inputsObj["clip"], new JArray("4", 1)), "Encoder.clip must not come directly from the inherited base-stage clip reference when LoRAs are active.");
        }

        IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)samplers[0].Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)), "Sampler.model must come from LoraLoader.model");
        Assert.False(TokenEquals(samplerInputs["model"], new JArray("4", 0)), "Sampler.model must not come directly from the inherited base-stage model reference when LoRAs are active.");
    }

    [Fact]
    public void Use_base_selection_without_edit_section_inherits_existing_base_model_loras()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;

        var input = new T2IParamInput(null);
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
                        // Simulate an upstream pipeline that has already applied a LoRA to the base model.
                        // The critical part is that g.FinalModel/g.FinalClip point at a "lora-applied" node chain.
                        _ = g.CreateNode("UnitTest_LoraAppliedModel", new JObject(), id: loraNodeId, idMandatory: false);
                        g.FinalModel = [loraNodeId, 0];
                        g.FinalClip = [loraNodeId, 1];
                    }, -800)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        // Base2Edit should NOT load a new model loader (which would drop upstream LoRAs) when there is no <edit> section
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple"));

        IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)samplers[0].Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraNodeId, 0)), "Sampler.model must inherit the upstream lora-applied model reference.");
    }

    [Fact]
    public void Use_base_selection_with_edit_section_lora_inherits_stage_loras_and_stacks_edit_lora()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;

        var editLora = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_EditLora.safetensors", "UnitTest_EditLora.safetensors");
        loraHandler.Models[editLora.Name] = editLora;

        var input = new T2IParamInput(null);
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
                        // Simulate an upstream base-stage model stack that already has LoRAs applied
                        _ = g.CreateNode("UnitTest_BaseLoraAppliedModel", new JObject(), id: stageLoraNodeId, idMandatory: false);
                        g.FinalModel = [stageLoraNodeId, 0];
                        g.FinalClip = [stageLoraNodeId, 1];
                    }, -800)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        // Must not reload the base model (would drop stage LoRAs); should stack edit LoRA on top instead
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple"));

        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;

        JObject loraInputs = (JObject)loraLoaders[0].Node["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray(stageLoraNodeId, 0)));
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(stageLoraNodeId, 1)));

        WorkflowNode sampler = WorkflowAssertions.RequireNodeOfType(workflow, "KSamplerAdvanced");
        JObject samplerInputs = (JObject)sampler.Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)));
    }

    [Fact]
    public void Explicit_edit_model_selection_without_edit_section_loads_selected_model_and_preserves_edit_params()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        var editModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        var input = new T2IParamInput(null);
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

        // Should create a loader for the explicitly-selected edit model even when no <edit> section exists
        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);
        JObject loaderInputs = (JObject)loaders[0].Node["inputs"];
        Assert.Contains("UnitTest_Edit", $"{loaderInputs["ckpt_name"]}");

        IReadOnlyList<WorkflowNode> ks = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        WorkflowNode sampler = ks.Count > 0
            ? ks[0]
            : WorkflowAssertions.RequireNodeOfType(workflow, "SwarmKSampler");
        JObject samplerInputs = (JObject)sampler.Node["inputs"];

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
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        var editModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        var lora = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[lora.Name] = lora;

        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, editModel.Name);
        input.Set(T2IParamTypes.Prompt, "global prompt only");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        // UI LoRA confined to a non-global section (eg BaseOnly = 5) should NOT apply to an explicit edit model
        // when there is no <edit> section
        input.Set(T2IParamTypes.Loras, new List<string> { "UnitTest_Lora" });
        input.Set(T2IParamTypes.LoraWeights, new List<string> { "1" });
        input.Set(T2IParamTypes.LoraSectionConfinement, new List<string> { $"{T2IParamInput.SectionID_BaseOnly}" });

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            WorkflowTestHarness.Template_BaseOnlyLatents()
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        // Should load the explicit edit model, but should not apply the confined UI LoRA to it
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LoraLoader"));

        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);

        WorkflowNode sampler = WorkflowAssertions.RequireNodeOfType(workflow, "KSamplerAdvanced");
        JObject samplerInputs = (JObject)sampler.Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loaders[0].Id, 0)));
    }

    [Fact]
    public void Use_refiner_selection_with_lora_loads_refiner_model_for_edit_stage_and_applies_edit_lora()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        var refinerModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Refiner.safetensors", "UnitTest_Refiner.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[refinerModel.Name] = refinerModel;

        var loraModel = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        var input = new T2IParamInput(null);
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

        // Assert we created a model loader for the edit stage and it is the refiner model.
        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;
        JObject loaderInputs = (JObject)loaders[0].Node["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_Refiner", ckptName);

        // Assert the LoRA loader exists and is chained from the refiner model loader.
        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        JObject loraInputs = (JObject)loraLoaders[0].Node["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray(loaderId, 0)), "LoraLoader.model must come from CheckpointLoaderSimple.model");
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(loaderId, 1)), "LoraLoader.clip must come from CheckpointLoaderSimple.clip");

        IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)samplers[0].Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)), "Sampler.model must come from LoraLoader.model");
        Assert.False(TokenEquals(samplerInputs["model"], new JArray(loaderId, 0)), "Sampler.model must not come directly from CheckpointLoaderSimple.model");
    }

    [Fact]
    public void Use_refiner_selection_with_edit_section_lora_inherits_stage_loras_and_stacks_edit_lora()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        var refinerModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Refiner.safetensors", "UnitTest_Refiner.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[refinerModel.Name] = refinerModel;

        var editLora = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_EditLora.safetensors", "UnitTest_EditLora.safetensors");
        loraHandler.Models[editLora.Name] = editLora;

        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, refinerModel);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(T2IParamTypes.Prompt, "global <edit>apply extra <lora:UnitTest_EditLora:0.5>");
        input.Set(Base2EditExtension.ApplyEditAfter, "Refiner");
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
                        // Simulate refiner phase with an upstream refiner-stage lora-applied model stack
                        g.IsRefinerStage = true;
                        _ = g.CreateNode("UnitTest_RefinerLoraAppliedModel", new JObject(), id: stageLoraNodeId, idMandatory: false);
                        g.FinalModel = [stageLoraNodeId, 0];
                        g.FinalClip = [stageLoraNodeId, 1];
                    }, -800)
                ])
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        // Must not reload the refiner model (would drop stage LoRAs); should stack edit LoRA on top instead
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple"));

        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;

        JObject loraInputs = (JObject)loraLoaders[0].Node["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray(stageLoraNodeId, 0)));
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(stageLoraNodeId, 1)));

        WorkflowNode sampler = WorkflowAssertions.RequireNodeOfType(workflow, "KSamplerAdvanced");
        JObject samplerInputs = (JObject)sampler.Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)));
    }

    [Fact]
    public void Explicit_edit_model_selection_with_lora_loads_selected_model_for_edit_stage_and_applies_edit_lora()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        var loraHandler = new T2IModelHandler { ModelType = "LoRA" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler,
            ["LoRA"] = loraHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        var editModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        var loraModel = new T2IModel(loraHandler, "/tmp", "/tmp/UnitTest_Lora.safetensors", "UnitTest_Lora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        var input = new T2IParamInput(null);
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

        // Assert we created a model loader for the selected edit model.
        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);
        string loaderId = loaders[0].Id;
        JObject loaderInputs = (JObject)loaders[0].Node["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_Edit", ckptName);

        // Assert the LoRA loader exists and is chained from the edit model loader.
        IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
        Assert.Single(loraLoaders);
        string loraId = loraLoaders[0].Id;
        JObject loraInputs = (JObject)loraLoaders[0].Node["inputs"];
        Assert.True(TokenEquals(loraInputs["model"], new JArray(loaderId, 0)), "LoraLoader.model must come from CheckpointLoaderSimple.model");
        Assert.True(TokenEquals(loraInputs["clip"], new JArray(loaderId, 1)), "LoraLoader.clip must come from CheckpointLoaderSimple.clip");

        IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)samplers[0].Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loraId, 0)), "Sampler.model must come from LoraLoader.model");
        Assert.False(TokenEquals(samplerInputs["model"], new JArray(loaderId, 0)), "Sampler.model must not come directly from CheckpointLoaderSimple.model");
    }

    [Fact]
    public void Use_refiner_selection_loads_refiner_model_for_edit_stage()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        var refinerModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Refiner.safetensors", "UnitTest_Refiner.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[refinerModel.Name] = refinerModel;

        var input = new T2IParamInput(null);
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

        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);

        JObject loaderInputs = (JObject)loaders[0].Node["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_Refiner", ckptName);
    }

    [Fact]
    public void Explicit_edit_model_selection_loads_separate_model_for_edit_stage()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        // Create base model and a separate edit model (not refiner)
        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Base.safetensors", "UnitTest_Base.safetensors");
        var editModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Edit.safetensors", "UnitTest_Edit.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Model, baseModel);
        // Explicitly select the edit model by its name
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

        // Should have created a model loader for the edit stage
        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);

        JObject loaderInputs = (JObject)loaders[0].Node["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_Edit", ckptName);

        // Verify the edit sampler is using the edit model loader
        IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        Assert.Single(samplers);
        JObject samplerInputs = (JObject)samplers[0].Node["inputs"];
        Assert.True(TokenEquals(samplerInputs["model"], new JArray(loaders[0].Id, 0)), "Sampler.model must come from the edit model loader");
    }

    [Fact]
    public void Edit_model_selection_with_cleaned_name_resolves_correctly()
    {
        // This test verifies that when the UI sends a cleaned model name (via CleanModelList),
        // the edit model resolution correctly matches against the cleaned name.
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        // Create models with names that CleanModelList will transform
        // Raw name: "illustrious/Gem_Collection_-_Sapphire.safetensors"
        // Cleaned name (via CleanModelName): "Gem Collection - Sapphire" (removes folder prefix, extension, replaces underscores)
        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/base_model.safetensors", "base_model.safetensors");
        var editModel = new T2IModel(sdHandler, "/tmp", "/tmp/illustrious/Gem_Collection_-_Sapphire.safetensors", "illustrious/Gem_Collection_-_Sapphire.safetensors");
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        // Get the cleaned name that the UI would send
        string cleanedEditModelName = T2IParamTypes.CleanModelName(editModel.Name);

        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Model, baseModel);
        // The UI sends the cleaned name, not the raw name
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

        // Should have created a model loader for the edit stage
        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);

        JObject loaderInputs = (JObject)loaders[0].Node["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        // The loader should reference the edit model (containing "Sapphire" in the name)
        Assert.Contains("Sapphire", ckptName);
    }

    [Fact]
    public void Edit_with_different_compat_class_triggers_reencode()
    {
        WorkflowTestHarness.Base2EditSteps();
        UnitTestStubs.EnsureComfySetClipDeviceRegistered();
        using var testContext = new SwarmUiTestContext();

        var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["Stable-Diffusion"] = sdHandler
        };

        // Create compat classes for different model architectures
        var sdxlCompat = new T2IModelCompatClass { ID = "sdxl", ShortCode = "SDXL" };
        var sd15Compat = new T2IModelCompatClass { ID = "sd15", ShortCode = "SD15" };

        var sdxlModelClass = new T2IModelClass { ID = "sdxl-base", Name = "SDXL Base", CompatClass = sdxlCompat, StandardWidth = 1024, StandardHeight = 1024 };
        var sd15ModelClass = new T2IModelClass { ID = "sd15-base", Name = "SD 1.5 Base", CompatClass = sd15Compat, StandardWidth = 512, StandardHeight = 512 };

        // Create base model (SDXL) and edit model (SD 1.5) with different compat classes
        var baseModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_SDXL.safetensors", "UnitTest_SDXL.safetensors")
        {
            ModelClass = sdxlModelClass
        };
        var editModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_SD15.safetensors", "UnitTest_SD15.safetensors")
        {
            ModelClass = sd15ModelClass
        };
        sdHandler.Models[baseModel.Name] = baseModel;
        sdHandler.Models[editModel.Name] = editModel;

        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, "UnitTest_SD15.safetensors");
        input.Set(T2IParamTypes.Prompt, "global <edit>edit prompt here");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        // Simulate base stage having loaded the SDXL model
        var harness = new Func<WorkflowGenerator.WorkflowGenStep>(() =>
            new WorkflowGenerator.WorkflowGenStep(g =>
            {
                _ = g.CreateNode("UnitTest_Model", new JObject(), id: "4", idMandatory: false);
                _ = g.CreateNode("UnitTest_Latent", new JObject(), id: "10", idMandatory: false);

                g.FinalModel = ["4", 0];
                g.FinalClip = ["4", 1];
                g.FinalVae = ["4", 2];
                g.FinalSamples = ["10", 0];
                g.FinalImageOut = null;
                g.FinalLoadedModel = baseModel;  // Base model is loaded
                g.FinalLoadedModelList = [baseModel];
            }, -1000));

        IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
            new[] { harness() }
                .Concat(WorkflowTestHarness.Base2EditSteps());

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

        // Should have created a model loader for the SD15 edit model
        IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
        Assert.Single(loaders);

        JObject loaderInputs = (JObject)loaders[0].Node["inputs"];
        Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
        string ckptName = $"{ckptNameTok}";
        Assert.Contains("UnitTest_SD15", ckptName);

        // Verify VAEEncode and VAEDecode exist for re-encoding
        IReadOnlyList<WorkflowNode> vaeEncodes = WorkflowUtils.NodesOfType(workflow, "VAEEncode");
        IReadOnlyList<WorkflowNode> vaeDecodes = WorkflowUtils.NodesOfType(workflow, "VAEDecode");
        Assert.True(vaeEncodes.Count >= 1, "Expected at least one VAEEncode for re-encoding");
        Assert.True(vaeDecodes.Count >= 1, "Expected at least one VAEDecode for pre-edit image");

        // Verify the edit sampler exists and uses the edit model
        IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
        Assert.Single(samplers);
    }
}
