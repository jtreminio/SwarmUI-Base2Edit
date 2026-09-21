using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using TextEncodeQwenImage21Node = Base2Edit.Generated.TextEncodeQwenImage21Node;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class QwenImage21Tests
{
    private static T2IParamInput BuildInput(string prompt)
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IModelHandler handler = new() { ModelType = "Stable-Diffusion" };
        T2IModel model = new(handler, "/tmp", "/tmp/Qwen21.safetensors", "Qwen21.safetensors")
        {
            ModelClass = new()
            {
                ID = "qwen-image-2.1",
                CompatClass = T2IModelClassSorter.CompatQwenImage21,
                StandardWidth = 1024,
                StandardHeight = 1024
            }
        };
        handler.Models[model.Name] = model;
        Program.T2IModelSets = new() { ["Stable-Diffusion"] = handler };

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, model);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.NegativePrompt, "blurry");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(Base2EditExtension.EditCFGScale, 4.0);
        return input;
    }

    private static WorkflowGenerator.WorkflowGenStep OriginalInputsStep() => new(g =>
    {
        string original = g.CreateNode("UnitTest_OriginalImage", [], id: "50");
        string cropped = g.CreateNode("UnitTest_CroppedImage", [], id: "51");
        g.BasicInputImage = new WGNodeData([original, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        g.MaskShrunkInfo = new(null, null, null, cropped);
    }, -800);

    private static (TextEncodeQwenImage21Node Encoder, List<INodeOutput> Images) AssertMatchingReferences(
        WorkflowBridge bridge, KSamplerAdvancedNode sampler, int count)
    {
        TextEncodeQwenImage21Node encoder = Assert.IsType<TextEncodeQwenImage21Node>(sampler.Positive.Connection.Node);
        Assert.Same(encoder.Positive, sampler.Positive.Connection);
        Assert.Same(encoder.Negative, sampler.Negative.Connection);
        Assert.Equal(0L, encoder.Resolution.LiteralAsLong());
        List<INodeOutput> images = encoder.Images.Items.Select(item => item.Connection).ToList();
        Assert.Equal(count, images.Count);
        Assert.DoesNotContain(images, image => image.Node.Id is "50" or "51");
        Assert.Empty(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.Empty(bridge.Graph.NodesOfType<Base2Edit.Generated.SwarmTextEncodeAdvancedNode>());
        return (encoder, images);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Edit_encodes_stage_pixels_and_shares_one_reference_on_both_cfg_branches(bool imageInput)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit>make it blue <b2eimage[base]>");
        var steps = imageInput ? WorkflowTestHarness.Template_EditOnly() : WorkflowTestHarness.Template_BaseOnlyLatents();
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input,
            steps.Append(OriginalInputsStep()).Concat(WorkflowTestHarness.Base2EditSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        var references = AssertMatchingReferences(bridge, sampler, 1);
        Assert.Equal("make it blue", references.Encoder.Prompt.LiteralAsString()?.Trim());
        Assert.Equal("blurry", references.Encoder.NegativePrompt.LiteralAsString()?.Trim());
        Assert.Equal("50", generator.BasicInputImage.Path[0].ToString());
        Assert.Equal("51", generator.MaskShrunkInfo.ScaledImage);
    }

    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public void Chained_edit_keeps_explicit_reference_before_current_stage_in_both_encodings(string applyAfter)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit[0]>first <edit[1]>second <b2eimage[base]> <b2eimage[edit0]>");
        input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        input.Set(Base2EditExtension.EditStages, new JArray(new JObject
        {
            ["applyAfter"] = "Edit Stage 0", ["model"] = ModelPrep.UseBase,
            ["steps"] = 20, ["cfgScale"] = 4.0, ["control"] = 1.0
        }).ToString());
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input,
            WorkflowTestHarness.Template_BaseOnlyLatents().Append(OriginalInputsStep()).Concat(WorkflowTestHarness.Base2EditSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        var samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Equal(2, samplers.Count);
        KSamplerAdvancedNode first = samplers.Single(s => s.LatentImage.Connection.Node.Id == "10");
        KSamplerAdvancedNode second = samplers.Single(s => s.LatentImage.Connection == first.Outputs[0]);
        AssertMatchingReferences(bridge, first, 1);
        var references = AssertMatchingReferences(bridge, second, 2);
        Assert.Equal("10", Assert.IsType<VAEDecodeNode>(references.Images[0].Node).Samples.Connection.Node.Id);
        Assert.Same(first.Outputs[0], Assert.IsType<VAEDecodeNode>(references.Images[1].Node).Samples.Connection);
    }

    [Fact]
    public void Switching_from_another_model_decodes_with_source_vae_and_encodes_with_qwen_vae()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit>make it blue <b2eimage[base]>");
        T2IModel editModel = input.Get(T2IParamTypes.Model);
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel baseModel = new(handler, "/tmp", "/tmp/SDXL.safetensors", "SDXL.safetensors")
        {
            ModelClass = new()
            {
                ID = "sdxl-base",
                CompatClass = new() { ID = "sdxl", ShortCode = "SDXL" },
                StandardWidth = 1024,
                StandardHeight = 1024
            }
        };
        handler.Models[baseModel.Name] = baseModel;
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, editModel.Name);
        T2IModelHandler clipHandler = new() { ModelType = "Clip" };
        T2IModel clip = new(clipHandler, "/tmp", "/tmp/Qwen21Clip.safetensors", "Qwen21Clip.safetensors");
        input.Set(T2IParamTypes.QwenModel, clip);
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        CommonModels.Known.TryGetValue("qwen-image-2.1-vae", out CommonModels.ModelInfo priorVae);
        CommonModels.ModelInfo knownVae = priorVae ?? new("qwen-image-2.1-vae", "Qwen VAE", "Test VAE",
            "https://example.invalid/vae", "", "VAE", "Qwen21Vae.safetensors");
        vaeHandler.Models[knownVae.FileName] = new(vaeHandler, "/tmp", $"/tmp/{knownVae.FileName}", knownVae.FileName);
        Program.T2IModelSets["VAE"] = vaeHandler;
        JObject workflow;
        try
        {
            CommonModels.Known[knownVae.ID] = knownVae;
            workflow = WorkflowTestHarness.GenerateWithSteps(input,
                WorkflowTestHarness.Template_BaseOnlyLatents().Concat(WorkflowTestHarness.Base2EditSteps()));
        }
        finally
        {
            if (priorVae is null)
            {
                CommonModels.Known.TryRemove(knownVae.ID, out CommonModels.ModelInfo removed);
            }
        }
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        var references = AssertMatchingReferences(bridge, sampler, 1);
        VAEDecodeNode source = Assert.IsType<VAEDecodeNode>(references.Images[0].Node);
        Assert.Equal("4", source.Vae.Connection.Node.Id);
        Assert.Equal("10", source.Samples.Connection.Node.Id);
        VAEEncodeNode converted = Assert.IsType<VAEEncodeNode>(sampler.LatentImage.Connection.Node);
        VAELoaderNode editVae = Assert.Single(bridge.Graph.NodesOfType<VAELoaderNode>());
        Assert.Same(editVae.Outputs[0], converted.Vae.Connection);
        Assert.Same(editVae.Outputs[0], references.Encoder.Vae.Connection);
        CLIPLoaderNode editClip = Assert.Single(bridge.Graph.NodesOfType<CLIPLoaderNode>());
        Assert.Same(editClip.Outputs[0], references.Encoder.Clip.Connection);
    }

    [Fact]
    public void Prompt_image_reference_uses_edit_vae_and_restores_prompt_images()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit>use this style <b2eimage[prompt0]>");
        Image promptImage = new(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3Z3ioAAAAASUVORK5CYII="), MediaType.ImagePng);
        input.Set(T2IParamTypes.PromptImages, new List<Image> { promptImage });
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input,
            WorkflowTestHarness.Template_BaseOnlyLatents().Append(OriginalInputsStep()).Concat(WorkflowTestHarness.Base2EditSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        var references = AssertMatchingReferences(bridge, Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>()), 2);
        Assert.IsType<LoadImageNode>(references.Images[0].Node);
        Assert.Equal("4", references.Encoder.Vae.Connection.Node.Id);
        Assert.Equal(2, references.Encoder.Vae.Connection.SlotIndex);
        Assert.Same(promptImage, Assert.Single(generator.UserInput.Get(T2IParamTypes.PromptImages)));
    }

    [Fact]
    public void Refine_only_omits_vision_images_and_all_reference_latents_even_with_init_inputs()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit>refine <b2eimage[base]>");
        input.Set(Base2EditExtension.EditRefineOnly, true);
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input,
            WorkflowTestHarness.Template_BaseOnlyLatents().Append(OriginalInputsStep()).Concat(WorkflowTestHarness.Base2EditSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        AssertMatchingReferences(bridge, Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>()), 0);
    }

    [Theory]
    [InlineData("Base", 0.9, 2)]
    [InlineData("Refiner", 0.9, 2)]
    [InlineData("Refiner", 1.0, 0)]
    public void Different_sized_identity_and_scene_references_stay_separate_and_preserve_edit_control(
        string applyAfter, double control, int startStep)
    {
        using SwarmUiTestContext _ = new();
        const string editPrompt = "Replace the person in <image2> with the person from <image1>.";
        T2IParamInput input = BuildInput($"young man <edit><b2eimage[prompt0]>{editPrompt}");
        input.Set(T2IParamTypes.Width, 832);
        input.Set(T2IParamTypes.Height, 1216);
        input.Set(T2IParamTypes.NegativePrompt, "");
        input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        input.Set(Base2EditExtension.EditControl, control);
        input.Set(Base2EditExtension.EditSteps, 20);
        using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> reference = new(1220, 1850);
        Image promptImage = new(reference);
        input.Set(T2IParamTypes.PromptImages, new List<Image> { promptImage });

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input,
            WorkflowTestHarness.Template_BaseOnlyLatents().Concat(WorkflowTestHarness.Base2EditSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        var references = AssertMatchingReferences(bridge, sampler, 2);
        LoadImageNode upload = Assert.IsType<LoadImageNode>(references.Images[0].Node);
        VAEDecodeNode scene = Assert.IsType<VAEDecodeNode>(references.Images[1].Node);
        Assert.Same(sampler.LatentImage.Connection, scene.Samples.Connection);
        JObject encoderInputs = (JObject)workflow[references.Encoder.Id]["inputs"];
        Assert.Equal(new JArray(upload.Id, 0), encoderInputs["images.image_1"]);
        Assert.Equal(new JArray(scene.Id, 0), encoderInputs["images.image_2"]);
        Assert.Equal(editPrompt, references.Encoder.Prompt.LiteralAsString());
        Assert.Equal("", references.Encoder.NegativePrompt.LiteralAsString());
        Assert.Equal((long)startStep, sampler.StartAtStep.LiteralAsLong());
        Assert.Equal(832, generator.CurrentMedia.Width);
        Assert.Equal(1216, generator.CurrentMedia.Height);
    }
}
