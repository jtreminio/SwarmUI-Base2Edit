using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using SwarmTextEncodeAdvancedNode = Base2Edit.Generated.SwarmTextEncodeAdvancedNode;

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

    private static (SwarmTextEncodeAdvancedNode Encoder, List<INodeOutput> Latents) ReadConditioning(INodeOutput output)
    {
        List<INodeOutput> latents = [];
        while (output.Node is ReferenceLatentNode reference)
        {
            latents.Insert(0, reference.Latent.Connection);
            output = reference.Conditioning.Connection;
        }
        return (Assert.IsType<SwarmTextEncodeAdvancedNode>(output.Node), latents);
    }

    private static List<INodeOutput> ReadImages(INodeOutput output)
    {
        if (output is null)
        {
            return [];
        }
        if (output.Node is BatchImagesNodeNode batch)
        {
            return batch.Images.Items.SelectMany(item => ReadImages(item.Connection)).ToList();
        }
        return [output];
    }

    private static (List<INodeOutput> Images, List<INodeOutput> Latents) AssertMatchingReferences(KSamplerAdvancedNode sampler, int count)
    {
        var positive = ReadConditioning(sampler.Positive.Connection);
        var negative = ReadConditioning(sampler.Negative.Connection);
        Assert.Equal(count, positive.Latents.Count);
        Assert.Equal(positive.Latents, negative.Latents);
        Assert.Same(positive.Encoder.Images.Connection, negative.Encoder.Images.Connection);
        List<INodeOutput> images = ReadImages(positive.Encoder.Images.Connection);
        Assert.Equal(count, images.Count);
        Assert.DoesNotContain(images, image => image.Node.Id is "50" or "51");
        for (int i = 0; i < count; i++)
        {
            if (images[i].Node is VAEDecodeNode decode)
            {
                // The current stage can retain its original latent; other references may be encoded anew.
                if (positive.Latents[i].Node is VAEEncodeNode encode)
                {
                    Assert.Same(images[i], encode.Pixels.Connection);
                }
                else
                {
                    Assert.Same(decode.Samples.Connection, positive.Latents[i]);
                }
            }
            else
            {
                Assert.Same(images[i], Assert.IsType<VAEEncodeNode>(positive.Latents[i].Node).Pixels.Connection);
            }
        }
        return (images, positive.Latents);
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
        var references = AssertMatchingReferences(sampler, 1);
        Assert.Same(sampler.LatentImage.Connection, references.Latents[0]);
        Assert.Equal("make it blue", ReadConditioning(sampler.Positive.Connection).Encoder.Prompt.LiteralAsString()?.Trim());
        Assert.Equal("blurry", ReadConditioning(sampler.Negative.Connection).Encoder.Prompt.LiteralAsString()?.Trim());
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
        AssertMatchingReferences(first, 1);
        var references = AssertMatchingReferences(second, 2);
        Assert.Equal("10", Assert.IsType<VAEDecodeNode>(references.Images[0].Node).Samples.Connection.Node.Id);
        Assert.Same(first.Outputs[0], references.Latents[1]);
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
        var references = AssertMatchingReferences(sampler, 1);
        VAEDecodeNode source = Assert.IsType<VAEDecodeNode>(references.Images[0].Node);
        Assert.Equal("4", source.Vae.Connection.Node.Id);
        Assert.Equal("10", source.Samples.Connection.Node.Id);
        VAEEncodeNode converted = Assert.IsType<VAEEncodeNode>(references.Latents[0].Node);
        VAELoaderNode editVae = Assert.Single(bridge.Graph.NodesOfType<VAELoaderNode>());
        Assert.Same(editVae.Outputs[0], converted.Vae.Connection);
        Assert.Same(converted.Outputs[0], sampler.LatentImage.Connection);
        CLIPLoaderNode editClip = Assert.Single(bridge.Graph.NodesOfType<CLIPLoaderNode>());
        Assert.Same(editClip.Outputs[0], ReadConditioning(sampler.Positive.Connection).Encoder.Clip.Connection);
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

        var references = AssertMatchingReferences(Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>()), 2);
        VAEEncodeNode encodedPrompt = Assert.IsType<VAEEncodeNode>(references.Latents[0].Node);
        Assert.Equal("4", encodedPrompt.Vae.Connection.Node.Id);
        Assert.Equal(2, encodedPrompt.Vae.Connection.SlotIndex);
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
        AssertMatchingReferences(Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>()), 0);
    }
}
