using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.WebAPI;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class Base2EditLoraWorkflowWiringTests
{
    private static bool TokenEquals(JToken a, JToken b) => JToken.DeepEquals(a, b);

    [Fact]
    public void Edit_prompt_lora_is_wired_between_model_loader_and_downstream_nodes()
    {
        _ = WorkflowTestHarness.Base2EditSteps();

        // Some WorkflowGenerator model-gen steps reference ComfyUIBackendExtension params directly.
        // In unit tests we don't initialize the full ComfyUIBackendExtension, so register the minimum
        // param(s) needed to avoid null derefs while still validating workflow JSON structure.
        if (ComfyUIBackendExtension.SetClipDevice is null)
        {
            ComfyUIBackendExtension.SetClipDevice = T2IParamTypes.Register<string>(new T2IParamType(
                Name: "Set CLIP Device (UnitTest Stub)",
                Description: "Stub param registered only for unit tests.",
                Default: "cpu",
                FeatureFlag: "set_clip_device",
                Group: T2IParamTypes.GroupAdvancedModelAddons,
                IsAdvanced: true,
                Toggleable: true,
                GetValues: (_) => ["cpu"]
            ));
        }

        Dictionary<string, T2IModelHandler> priorModelSets = Program.T2IModelSets;
        bool priorIncludeHash = Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash;
        List<WorkflowGenerator.WorkflowGenStep> priorModelGenSteps = [.. WorkflowGenerator.ModelGenSteps];
        ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>> priorExtraModelProviders = ModelsAPI.ExtraModelProviders;

        try
        {
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = false;
            ModelsAPI.ExtraModelProviders = new ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>>(
                [
                    new KeyValuePair<string, Func<string, Dictionary<string, JObject>>>("unit_test", _ => new Dictionary<string, JObject>())
                ]);

            // This test is only about verifying the resulting JSON node graph wiring
            WorkflowGenerator.ModelGenSteps = [];

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
            input.Set(Base2EditExtension.ApplyEditAfter, "Base");
            input.Set(T2IParamTypes.Seed, 1L);
            input.Set(T2IParamTypes.Width, 512);
            input.Set(T2IParamTypes.Height, 512);

            IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
                new[] { WorkflowTestHarness.MinimalGraphSeedStep() }
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
        finally
        {
            WorkflowGenerator.ModelGenSteps = priorModelGenSteps;
            ModelsAPI.ExtraModelProviders = priorExtraModelProviders;
            Program.T2IModelSets = priorModelSets;
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = priorIncludeHash;
        }
    }

    [Fact]
    public void Edit_prompt_without_lora_uses_model_directly_without_lora_loader()
    {
        _ = WorkflowTestHarness.Base2EditSteps();

        if (ComfyUIBackendExtension.SetClipDevice is null)
        {
            ComfyUIBackendExtension.SetClipDevice = T2IParamTypes.Register<string>(new T2IParamType(
                Name: "Set CLIP Device (UnitTest Stub)",
                Description: "Stub param registered only for unit tests.",
                Default: "cpu",
                FeatureFlag: "set_clip_device",
                Group: T2IParamTypes.GroupAdvancedModelAddons,
                IsAdvanced: true,
                Toggleable: true,
                GetValues: (_) => ["cpu"]
            ));
        }

        Dictionary<string, T2IModelHandler> priorModelSets = Program.T2IModelSets;
        bool priorIncludeHash = Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash;
        List<WorkflowGenerator.WorkflowGenStep> priorModelGenSteps = [.. WorkflowGenerator.ModelGenSteps];
        ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>> priorExtraModelProviders = ModelsAPI.ExtraModelProviders;

        try
        {
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = false;
            ModelsAPI.ExtraModelProviders = new ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>>(
                [
                    new KeyValuePair<string, Func<string, Dictionary<string, JObject>>>("unit_test", _ => new Dictionary<string, JObject>())
                ]);
            WorkflowGenerator.ModelGenSteps = [];

            var sdHandler = new T2IModelHandler { ModelType = "Stable-Diffusion" };
            Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
            {
                ["Stable-Diffusion"] = sdHandler
            };

            var sdModel = new T2IModel(sdHandler, "/tmp", "/tmp/UnitTest_Model.safetensors", "UnitTest_Model.safetensors");
            sdHandler.Models[sdModel.Name] = sdModel;

            var input = new T2IParamInput(null);
            input.Set(T2IParamTypes.Model, sdModel);
            input.Set(Base2EditExtension.EditModel, "(Use Base)");
            input.Set(T2IParamTypes.Prompt, "global <edit>no lora here");
            input.Set(Base2EditExtension.ApplyEditAfter, "Base");
            input.Set(T2IParamTypes.Seed, 1L);
            input.Set(T2IParamTypes.Width, 512);
            input.Set(T2IParamTypes.Height, 512);

            IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
                new[] { WorkflowTestHarness.MinimalGraphSeedStep() }
                    .Concat(WorkflowTestHarness.Base2EditSteps());

            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

            IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
            Assert.Single(loaders);
            string loaderId = loaders[0].Id;

            IReadOnlyList<WorkflowNode> loraLoaders = WorkflowUtils.NodesOfType(workflow, "LoraLoader");
            Assert.Empty(loraLoaders);

            // Assert downstream nodes use the raw loader outputs.
            IReadOnlyList<WorkflowNode> encoders = WorkflowUtils.NodesOfType(workflow, "SwarmClipTextEncodeAdvanced");
            Assert.True(encoders.Count >= 2, "Expected at least positive+negative SwarmClipTextEncodeAdvanced nodes.");
            foreach (WorkflowNode enc in encoders)
            {
                JObject inputsObj = (JObject)enc.Node["inputs"];
                Assert.True(TokenEquals(inputsObj["clip"], new JArray(loaderId, 1)), "Encoder.clip must come from CheckpointLoaderSimple.clip");
            }

            IReadOnlyList<WorkflowNode> samplers = WorkflowUtils.NodesOfType(workflow, "KSamplerAdvanced");
            Assert.Single(samplers);
            JObject samplerInputs = (JObject)samplers[0].Node["inputs"];
            Assert.True(TokenEquals(samplerInputs["model"], new JArray(loaderId, 0)), "Sampler.model must come from CheckpointLoaderSimple.model");
        }
        finally
        {
            WorkflowGenerator.ModelGenSteps = priorModelGenSteps;
            ModelsAPI.ExtraModelProviders = priorExtraModelProviders;
            Program.T2IModelSets = priorModelSets;
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = priorIncludeHash;
        }
    }

    [Fact]
    public void Use_refiner_selection_loads_refiner_model_for_edit_stage()
    {
        _ = WorkflowTestHarness.Base2EditSteps();

        if (ComfyUIBackendExtension.SetClipDevice is null)
        {
            ComfyUIBackendExtension.SetClipDevice = T2IParamTypes.Register<string>(new T2IParamType(
                Name: "Set CLIP Device (UnitTest Stub)",
                Description: "Stub param registered only for unit tests.",
                Default: "cpu",
                FeatureFlag: "set_clip_device",
                Group: T2IParamTypes.GroupAdvancedModelAddons,
                IsAdvanced: true,
                Toggleable: true,
                GetValues: (_) => ["cpu"]
            ));
        }

        Dictionary<string, T2IModelHandler> priorModelSets = Program.T2IModelSets;
        bool priorIncludeHash = Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash;
        List<WorkflowGenerator.WorkflowGenStep> priorModelGenSteps = [.. WorkflowGenerator.ModelGenSteps];
        ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>> priorExtraModelProviders = ModelsAPI.ExtraModelProviders;

        try
        {
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = false;
            ModelsAPI.ExtraModelProviders = new ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>>(
                [
                    new KeyValuePair<string, Func<string, Dictionary<string, JObject>>>("unit_test", _ => new Dictionary<string, JObject>())
                ]);
            WorkflowGenerator.ModelGenSteps = [];

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
            input.Set(Base2EditExtension.EditModel, "(Use Refiner)");
            input.Set(T2IParamTypes.Prompt, "global <edit>no lora here");
            input.Set(Base2EditExtension.ApplyEditAfter, "Base");
            input.Set(T2IParamTypes.Seed, 1L);
            input.Set(T2IParamTypes.Width, 512);
            input.Set(T2IParamTypes.Height, 512);

            IEnumerable<WorkflowGenerator.WorkflowGenStep> steps =
                new[] { WorkflowTestHarness.MinimalGraphSeedStep() }
                    .Concat(WorkflowTestHarness.Base2EditSteps());

            JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, steps);

            IReadOnlyList<WorkflowNode> loaders = WorkflowUtils.NodesOfType(workflow, "CheckpointLoaderSimple");
            Assert.Single(loaders);

            JObject loaderInputs = (JObject)loaders[0].Node["inputs"];
            Assert.True(loaderInputs.TryGetValue("ckpt_name", out JToken ckptNameTok), "Expected CheckpointLoaderSimple.inputs.ckpt_name");
            string ckptName = $"{ckptNameTok}";
            Assert.Contains("UnitTest_Refiner", ckptName);
        }
        finally
        {
            WorkflowGenerator.ModelGenSteps = priorModelGenSteps;
            ModelsAPI.ExtraModelProviders = priorExtraModelProviders;
            Program.T2IModelSets = priorModelSets;
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = priorIncludeHash;
        }
    }
}
