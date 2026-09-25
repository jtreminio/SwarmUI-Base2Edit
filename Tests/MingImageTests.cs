using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class MingImageTests
{
    private static T2IParamInput BuildInput(string prompt)
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IModelHandler handler = new() { ModelType = "Stable-Diffusion" };
        T2IModel model = new(handler, "/tmp", "/tmp/Ming.safetensors", "Ming.safetensors")
        {
            ModelClass = new()
            {
                ID = "ming-image",
                CompatClass = T2IModelClassSorter.CompatMingImage,
                StandardWidth = 2048,
                StandardHeight = 2048
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
        return input;
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> Steps(bool imageInput) =>
        (imageInput ? WorkflowTestHarness.Template_EditOnly() : WorkflowTestHarness.Template_BaseOnlyLatents())
            .Concat(WorkflowTestHarness.Base2EditSteps());

    private static (ReferenceLatentNode Reference, SwarmClipTextEncodeAdvancedNode Encoder) PositiveEncoder(
        KSamplerAdvancedNode sampler)
    {
        ReferenceLatentNode reference = Assert.IsType<ReferenceLatentNode>(sampler.Positive.Connection?.Node);
        SwarmClipTextEncodeAdvancedNode encoder = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(reference.Conditioning.Connection?.Node);
        return (reference, encoder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Edit_sends_current_image_to_positive_encoder_and_reference_latent(bool imageInput)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit>make it blue");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, Steps(imageInput));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        var (reference, encoder) = PositiveEncoder(sampler);
        Assert.Equal("make it blue", encoder.Prompt.LiteralAsString()?.Trim());
        Assert.NotNull(encoder.Images.Connection);
        Assert.Equal("blurry", Assert.IsType<SwarmClipTextEncodeAdvancedNode>(sampler.Negative.Connection?.Node).Prompt.LiteralAsString());
        Assert.Null(Assert.IsType<SwarmClipTextEncodeAdvancedNode>(sampler.Negative.Connection?.Node).Images.Connection);
        Assert.Same(sampler.LatentImage.Connection, reference.Latent.Connection);
        Assert.Empty(bridge.Graph.NodesOfType<ImageBatchNode>());
        Assert.Equal(imageInput ? "11" : "10", imageInput
            ? encoder.Images.Connection.Node.Id
            : Assert.IsType<VAEDecodeNode>(encoder.Images.Connection.Node).Samples.Connection.Node.Id);
    }

    [Fact]
    public void Chained_edit_batches_explicit_base_image_before_current_stage_image()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit[0]>first <edit[1]>second <b2eimage[base]>");
        input.Set(Base2EditExtension.EditStages, new JArray(new JObject
        {
            ["applyAfter"] = "Edit Stage 0", ["model"] = ModelPrep.UseBase,
            ["steps"] = 20, ["cfgScale"] = 4.0, ["control"] = 1.0
        }).ToString());
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, Steps(imageInput: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<KSamplerAdvancedNode> samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Equal(2, samplers.Count);
        KSamplerAdvancedNode first = samplers.Single(s => s.LatentImage.Connection.Node.Id == "10");
        KSamplerAdvancedNode second = samplers.Single(s => s.LatentImage.Connection == first.Outputs[0]);
        ReferenceLatentNode currentReference = Assert.IsType<ReferenceLatentNode>(second.Positive.Connection?.Node);
        ReferenceLatentNode baseReference = Assert.IsType<ReferenceLatentNode>(currentReference.Conditioning.Connection?.Node);
        Assert.Same(first.Outputs[0], currentReference.Latent.Connection);
        Assert.Equal("10", baseReference.Latent.Connection?.Node.Id);
        SwarmClipTextEncodeAdvancedNode encoder = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(baseReference.Conditioning.Connection?.Node);
        ImageBatchNode batch = Assert.IsType<ImageBatchNode>(encoder.Images.Connection?.Node);
        ImageScaleNode baseImage = Assert.IsType<ImageScaleNode>(batch.Image1.Connection?.Node);
        ImageScaleNode currentImage = Assert.IsType<ImageScaleNode>(batch.Image2.Connection?.Node);
        Assert.Equal("10", Assert.IsType<VAEDecodeNode>(baseImage.Image.Connection?.Node).Samples.Connection?.Node.Id);
        Assert.Same(first.Outputs[0], Assert.IsType<VAEDecodeNode>(currentImage.Image.Connection?.Node).Samples.Connection);
        Assert.Equal(512, baseImage.Width.LiteralAsInt());
        Assert.Equal(512, currentImage.Height.LiteralAsInt());
    }

    [Fact]
    public void Refine_only_omits_ming_images_and_references()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit>make it blue <b2eimage[base]>");
        input.Set(Base2EditExtension.EditRefineOnly, true);
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, Steps(imageInput: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        SwarmClipTextEncodeAdvancedNode encoder = Assert.IsType<SwarmClipTextEncodeAdvancedNode>(sampler.Positive.Connection?.Node);
        Assert.Null(encoder.Images.Connection);
        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageBatchNode>());
    }

    [Fact]
    public void Specific_ming_edit_model_decodes_with_source_vae_and_encodes_with_ming_vae()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit>make it blue");
        T2IModel mingModel = input.Get(T2IParamTypes.Model);
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
        input.Set(Base2EditExtension.EditModel, mingModel.Name);

        T2IModelHandler clipHandler = new() { ModelType = "Clip" };
        const string clipName = "ming_image_0.1_ling_mini_2.0_int8_convrot.safetensors";
        clipHandler.Models[clipName] = new(clipHandler, "/tmp", $"/tmp/{clipName}", clipName);
        Program.T2IModelSets["Clip"] = clipHandler;
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        CommonModels.Known.TryGetValue("ming-image-vae", out CommonModels.ModelInfo priorVae);
        CommonModels.ModelInfo knownVae = priorVae ?? new("ming-image-vae", "Ming VAE", "Test VAE",
            "https://example.invalid/vae", "", "VAE", "MingVae.safetensors");
        string vaeName = knownVae.FileName;
        vaeHandler.Models[vaeName] = new(vaeHandler, "/tmp", $"/tmp/{vaeName}", vaeName);
        Program.T2IModelSets["VAE"] = vaeHandler;

        JObject workflow;
        try
        {
            CommonModels.Known[knownVae.ID] = knownVae;
            workflow = WorkflowTestHarness.GenerateWithSteps(input, Steps(imageInput: false));
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
        var (reference, encoder) = PositiveEncoder(sampler);
        VAEDecodeNode source = Assert.IsType<VAEDecodeNode>(encoder.Images.Connection?.Node);
        Assert.Equal("4", source.Vae.Connection?.Node.Id);
        Assert.Equal("10", source.Samples.Connection?.Node.Id);
        VAELoaderNode mingVae = Assert.Single(bridge.Graph.NodesOfType<VAELoaderNode>());
        VAEEncodeNode converted = Assert.IsType<VAEEncodeNode>(sampler.LatentImage.Connection?.Node);
        Assert.Same(mingVae.Outputs[0], converted.Vae.Connection);
        Assert.Same(converted.Outputs[0], reference.Latent.Connection);
        CLIPLoaderNode mingClip = Assert.Single(bridge.Graph.NodesOfType<CLIPLoaderNode>());
        Assert.Same(mingClip.Outputs[0], encoder.Clip.Connection);
    }
}
