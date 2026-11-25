using ComfyTyped.Core;
using ComfyTyped.Generated;
using ImageMagick;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class Krea2Tests
{
    private static T2IParamInput BuildInput(string prompt = "global <edit>make it blue <b2eimage[base]>")
    {
        WorkflowTestHarness.Base2EditSteps();
        ComfyUIBackendExtension.EnableReferenceLatents ??= T2IParamTypes.Register<string>(new(
            "Enable Reference Latents", "Test parameter", "none"));
        ComfyUIBackendExtension.TextEncodedImage ??= T2IParamTypes.Register<string>(new(
            "Text Encoded Image", "Test parameter", "auto"));
        T2IModelHandler handler = new() { ModelType = "Stable-Diffusion" };
        T2IModel model = new(handler, "/tmp", "/tmp/Krea2.safetensors", "Krea2.safetensors")
        {
            ModelClass = new()
            {
                ID = "krea-2", CompatClass = T2IModelClassSorter.CompatKrea2,
                StandardWidth = 1024, StandardHeight = 1024
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
        input.Set(T2IParamTypes.Height, 768);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(Base2EditExtension.EditCFGScale, 4.0);
        return input;
    }

    private static JObject Generate(T2IParamInput input, bool imageInput = false) =>
        WorkflowTestHarness.GenerateWithSteps(input,
            (imageInput ? WorkflowTestHarness.Template_EditOnly() : WorkflowTestHarness.Template_BaseOnlyLatents())
            .Concat(WorkflowTestHarness.Base2EditSteps()));

    [Theory]
    [InlineData(false, "Base")]
    [InlineData(true, "Base")]
    [InlineData(false, "Refiner")]
    [InlineData(true, "Refiner")]
    public void Base_model_encodes_stage_image_on_both_branches_without_reference_latents(bool imageInput, string applyAfter)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput();
        input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        using WorkflowBridge bridge = WorkflowBridge.Create(Generate(input, imageInput));
        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        SwarmTextEncodeAdvancedNode positive = Assert.IsType<SwarmTextEncodeAdvancedNode>(sampler.Positive.Connection.Node);
        SwarmTextEncodeAdvancedNode negative = Assert.IsType<SwarmTextEncodeAdvancedNode>(sampler.Negative.Connection.Node);
        Assert.Equal("make it blue", positive.Prompt.LiteralAsString()?.Trim());
        Assert.Equal("blurry", negative.Prompt.LiteralAsString());
        Assert.Equal("krea2", positive.LlamaTemplate.LiteralAsString());
        Assert.Equal("krea2", negative.LlamaTemplate.LiteralAsString());
        Assert.NotNull(positive.Images.Connection);
        Assert.Same(positive.Images.Connection, negative.Images.Connection);
        ImageScaleToTotalPixelsNode scale = Assert.IsType<ImageScaleToTotalPixelsNode>(positive.Images.Connection.Node);
        SplitImageWithAlphaNode rgb = Assert.IsType<SplitImageWithAlphaNode>(scale.Image.Connection.Node);
        Assert.Equal(imageInput ? "11" : "10", imageInput ? rgb.Image.Connection.Node.Id
            : Assert.IsType<VAEDecodeNode>(rgb.Image.Connection.Node).Samples.Connection.Node.Id);
        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageBatchNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>());
        SwarmAttnTokenWeightsNode weights = Assert.IsType<SwarmAttnTokenWeightsNode>(sampler.Model.Connection.Node);
        Assert.Same(sampler.Positive.Connection, weights.Conditioning.Connection);
    }

    [Theory]
    [InlineData("none", "auto", false, 0, true)]
    [InlineData("index", "auto", false, 1, true)]
    [InlineData("index_timestep_zero", "none", false, 1, false)]
    [InlineData("none", "none", false, 0, false)]
    [InlineData("index", "auto", true, 0, false)]
    public void Reference_and_text_image_settings_are_independent_and_refine_only_disables_both(
        string method, string textMode, bool refineOnly, int referenceCount, bool hasImage)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput();
        input.Set(ComfyUIBackendExtension.EnableReferenceLatents, method);
        input.Set(ComfyUIBackendExtension.TextEncodedImage, textMode);
        input.Set(Base2EditExtension.EditRefineOnly, refineOnly);
        using WorkflowBridge bridge = WorkflowBridge.Create(Generate(input));
        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        Assert.Equal(referenceCount, bridge.Graph.NodesOfType<ReferenceLatentNode>().Count);
        foreach (SwarmTextEncodeAdvancedNode encoder in bridge.Graph.NodesOfType<SwarmTextEncodeAdvancedNode>())
        {
            Assert.Equal(hasImage, encoder.Images.Connection is not null);
        }
        Assert.IsType<SwarmTextEncodeAdvancedNode>(sampler.Negative.Connection.Node);
        if (referenceCount > 0)
        {
            FluxKontextMultiReferenceLatentMethodNode selector = Assert.IsType<FluxKontextMultiReferenceLatentMethodNode>(sampler.Positive.Connection.Node);
            Assert.Equal(method, selector.ReferenceLatentsMethod.LiteralAsString());
            ReferenceLatentNode reference = Assert.IsType<ReferenceLatentNode>(selector.Conditioning.Connection.Node);
            Assert.Same(sampler.LatentImage.Connection, reference.Latent.Connection);
        }
        else
        {
            Assert.Empty(bridge.Graph.NodesOfType<FluxKontextMultiReferenceLatentMethodNode>());
        }
    }

    [Theory]
    [InlineData("auto", true, 384 * 384)]
    [InlineData("small", false, 384 * 384)]
    [InlineData("large", true, 1024 * 1024)]
    [InlineData("auto", false, 512 * 768)]
    public void Text_image_resolution_matches_core_settings(string mode, bool smartResize, int pixels)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput();
        input.Set(ComfyUIBackendExtension.TextEncodedImage, mode);
        input.Set(T2IParamTypes.SmartImagePromptResizing, smartResize);
        JObject workflow = Generate(input);
        JObject scale = Assert.Single(workflow.Properties().Select(p => (JObject)p.Value),
            node => node["class_type"]?.ToString() == ImageScaleToTotalPixelsNode.ClassType);
        // ComfyUI interprets megapixels as 1024 * 1024 pixels.
        double outputPixels = scale["inputs"]!["megapixels"]!.Value<double>() * 1024 * 1024;
        Assert.Equal(pixels, outputPixels, 8);
    }

    [Fact]
    public void Chained_edit_keeps_explicit_base_reference_before_current_stage()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit[0]>first <edit[1]>second <b2eimage[base]> <b2eimage[edit0]>");
        input.Set(ComfyUIBackendExtension.EnableReferenceLatents, "index");
        input.Set(Base2EditExtension.EditStages, new JArray(new JObject
        {
            ["applyAfter"] = "Edit Stage 0", ["model"] = ModelPrep.UseBase,
            ["steps"] = 20, ["cfgScale"] = 4.0, ["control"] = 1.0
        }).ToString());
        using WorkflowBridge bridge = WorkflowBridge.Create(Generate(input));
        var samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        Assert.Equal(2, samplers.Count);
        KSamplerAdvancedNode first = samplers.Single(s => s.LatentImage.Connection.Node.Id == "10");
        KSamplerAdvancedNode second = samplers.Single(s => s.LatentImage.Connection == first.Outputs[0]);
        FluxKontextMultiReferenceLatentMethodNode selector = Assert.IsType<FluxKontextMultiReferenceLatentMethodNode>(second.Positive.Connection.Node);
        ReferenceLatentNode current = Assert.IsType<ReferenceLatentNode>(selector.Conditioning.Connection.Node);
        ReferenceLatentNode earlier = Assert.IsType<ReferenceLatentNode>(current.Conditioning.Connection.Node);
        Assert.Same(first.Outputs[0], current.Latent.Connection);
        Assert.Equal("10", earlier.Latent.Connection.Node.Id);
        SwarmTextEncodeAdvancedNode encoder = Assert.IsType<SwarmTextEncodeAdvancedNode>(earlier.Conditioning.Connection.Node);
        ImageBatchNode batch = Assert.IsType<ImageBatchNode>(encoder.Images.Connection.Node);
        VAEDecodeNode decode(INodeOutput image) => Assert.IsType<VAEDecodeNode>(
            Assert.IsType<SplitImageWithAlphaNode>(Assert.IsType<ImageScaleToTotalPixelsNode>(image.Node).Image.Connection.Node).Image.Connection.Node);
        Assert.Equal("10", decode(batch.Image1.Connection).Samples.Connection.Node.Id);
        Assert.Same(first.Outputs[0], decode(batch.Image2.Connection).Samples.Connection);
        Assert.Same(encoder.Images.Connection,
            Assert.IsType<SwarmTextEncodeAdvancedNode>(second.Negative.Connection.Node).Images.Connection);
    }

    [Fact]
    public void Switching_from_sdxl_uses_krea_clip_and_qwen_vae_without_redecoding_source_latents_with_edit_vae()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput();
        T2IModel editModel = input.Get(T2IParamTypes.Model);
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel baseModel = new(handler, "/tmp", "/tmp/SDXL.safetensors", "SDXL.safetensors")
        {
            ModelClass = new()
            {
                ID = "sdxl-base", CompatClass = new() { ID = "sdxl", ShortCode = "SDXL" },
                StandardWidth = 1024, StandardHeight = 1024
            }
        };
        handler.Models[baseModel.Name] = baseModel;
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(Base2EditExtension.EditModel, editModel.Name);
        input.Set(Base2EditExtension.EditReferenceLatents, "index_timestep_zero");
        Assert.Equal("comfyui", Base2EditExtension.EditReferenceLatents.Type.FeatureFlag);
        T2IModelHandler clipHandler = new() { ModelType = "Clip" };
        T2IModel clip = new(clipHandler, "/tmp", "/tmp/KreaClip.safetensors", "KreaClip.safetensors");
        input.Set(T2IParamTypes.QwenModel, clip);
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        CommonModels.Known.TryGetValue("qwen-image-vae", out CommonModels.ModelInfo priorVae);
        CommonModels.ModelInfo knownVae = priorVae ?? new("qwen-image-vae", "Qwen VAE", "Test VAE",
            "https://example.invalid/vae", "", "VAE", "QwenVae.safetensors");
        vaeHandler.Models[knownVae.FileName] = new(vaeHandler, "/tmp", $"/tmp/{knownVae.FileName}", knownVae.FileName);
        Program.T2IModelSets["VAE"] = vaeHandler;
        JObject workflow;
        try
        {
            CommonModels.Known[knownVae.ID] = knownVae;
            workflow = Generate(input);
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
        VAEEncodeNode converted = Assert.IsType<VAEEncodeNode>(sampler.LatentImage.Connection.Node);
        VAEDecodeNode source = Assert.IsType<VAEDecodeNode>(converted.Pixels.Connection.Node);
        Assert.Equal("4", source.Vae.Connection.Node.Id);
        Assert.Equal("10", source.Samples.Connection.Node.Id);
        Assert.Same(Assert.Single(bridge.Graph.NodesOfType<VAELoaderNode>()).Outputs[0], converted.Vae.Connection);
        ReferenceLatentNode reference = Assert.Single(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.Same(converted.Outputs[0], reference.Latent.Connection);
        SwarmTextEncodeAdvancedNode encoder = Assert.IsType<SwarmTextEncodeAdvancedNode>(reference.Conditioning.Connection.Node);
        CLIPLoaderNode clipLoader = Assert.Single(bridge.Graph.NodesOfType<CLIPLoaderNode>());
        Assert.Equal("krea2", workflow[clipLoader.Id]!["inputs"]!["type"]!.ToString());
        Assert.Same(clipLoader.Outputs[0], encoder.Clip.Connection);
        ImageScaleToTotalPixelsNode scale = Assert.IsType<ImageScaleToTotalPixelsNode>(encoder.Images.Connection.Node);
        Assert.Same(source.IMAGE, Assert.IsType<SplitImageWithAlphaNode>(scale.Image.Connection.Node).Image.Connection);
    }

    [Theory]
    [InlineData(2048, 1024, true)]
    [InlineData(512, 768, false)]
    public void Uploaded_reference_matches_stage_area_before_vae_encoding(int width, int height, bool needsResize)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit>use this <b2eimage[prompt0]>");
        input.Set(Base2EditExtension.EditReferenceLatents, "index");
        input.Set(ComfyUIBackendExtension.TextEncodedImage, "none");
        using MagickImage pixels = new(MagickColors.Black, (uint)width, (uint)height);
        Image uploaded = new(pixels);
        input.Set(T2IParamTypes.PromptImages, new List<Image> { uploaded });
        using WorkflowBridge bridge = WorkflowBridge.Create(Generate(input));
        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        FluxKontextMultiReferenceLatentMethodNode method = Assert.IsType<FluxKontextMultiReferenceLatentMethodNode>(sampler.Positive.Connection.Node);
        ReferenceLatentNode current = Assert.IsType<ReferenceLatentNode>(method.Conditioning.Connection.Node);
        ReferenceLatentNode reference = Assert.IsType<ReferenceLatentNode>(current.Conditioning.Connection.Node);
        Assert.Same(sampler.LatentImage.Connection, current.Latent.Connection);
        VAEEncodeNode encoded = Assert.IsType<VAEEncodeNode>(reference.Latent.Connection.Node);
        if (needsResize)
        {
            ImageScaleNode scale = Assert.IsType<ImageScaleNode>(encoded.Pixels.Connection.Node);
            Assert.Equal(896, scale.Width.LiteralAsInt());
            Assert.Equal(448, scale.Height.LiteralAsInt());
            Assert.IsType<LoadImageNode>(scale.Image.Connection.Node);
        }
        else
        {
            Assert.IsType<LoadImageNode>(encoded.Pixels.Connection.Node);
            Assert.Empty(bridge.Graph.NodesOfType<ImageScaleNode>());
        }
        Assert.Same(uploaded, Assert.Single(input.Get(T2IParamTypes.PromptImages)));
    }

    [Fact]
    public void Stage_reference_mode_overrides_core_and_is_independent_between_stages()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("global <edit[0]>first <edit[1]>second");
        input.Set(ComfyUIBackendExtension.EnableReferenceLatents, "index_timestep_zero");
        input.Set(Base2EditExtension.EditReferenceLatents, "none");
        input.Set(Base2EditExtension.EditStages, new JArray(new JObject
        {
            ["applyAfter"] = "Edit Stage 0", ["model"] = ModelPrep.UseBase,
            ["referenceLatents"] = "index"
        }).ToString());
        using WorkflowBridge bridge = WorkflowBridge.Create(Generate(input));
        var samplers = bridge.Graph.NodesOfType<KSamplerAdvancedNode>();
        KSamplerAdvancedNode first = samplers.Single(s => s.LatentImage.Connection.Node.Id == "10");
        KSamplerAdvancedNode second = samplers.Single(s => s.LatentImage.Connection == first.Outputs[0]);
        Assert.IsType<SwarmTextEncodeAdvancedNode>(first.Positive.Connection.Node);
        FluxKontextMultiReferenceLatentMethodNode method = Assert.IsType<FluxKontextMultiReferenceLatentMethodNode>(second.Positive.Connection.Node);
        Assert.Equal("index", method.ReferenceLatentsMethod.LiteralAsString());
    }

    [Fact]
    public void Reference_mode_supports_section_overrides_with_existing_precedence()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput();
        input.Set(Base2EditExtension.EditReferenceLatents, "none");
        input.Set(Base2EditExtension.EditReferenceLatents, "index", Base2EditExtension.EditSectionIdForStage(0));
        input.Set(Base2EditExtension.EditStages, new JArray(new JObject
        {
            ["applyAfter"] = "Edit Stage 0", ["model"] = ModelPrep.UseBase
        }).ToString());
        List<StageSpec> stages = Base2EditSpecParser.Parse(new WorkflowGenerator { UserInput = input });
        Assert.Equal("index", stages[0].ReferenceLatents);
        Assert.Equal("index", stages[1].ReferenceLatents);
    }
}
