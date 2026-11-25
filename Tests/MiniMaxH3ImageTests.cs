using ComfyTyped.Core;
using ComfyTyped.Families;
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
public class MiniMaxH3ImageTests
{
    private const string EditModelName = "MiniMax.safetensors";
    private const string EditVaeId = "minimax-h3-image-vae";

    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public void MiniMax_to_MiniMax_samples_from_the_base_latent_and_keeps_native_references(string applyAfter)
    {
        JObject workflow = Generate(applyAfter, "base <edit>edit it <b2eimage[prompt0]>", input =>
        {
            input.Set(Base2EditExtension.KeepPreEditImage, true);
            input.Set(T2IParamTypes.VAETileSize, 512);
            using MagickImage pixels = new(MagickColors.Black, 16, 16);
            input.Set(T2IParamTypes.PromptImages, new List<Image> { new(pixels) });
        }, miniMaxBase: true);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmKSamplerNode baseSampler = Assert.IsType<SwarmKSamplerNode>(bridge.Graph.GetNode("10"));
        SwarmKSamplerNode edit = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>(), sampler => sampler.Id != "10");
        Assert.Same(baseSampler.Outputs[0], edit.LatentImage.Connection);
        SwarmTextEncodeAdvancedNode positive = Assert.IsType<SwarmTextEncodeAdvancedNode>(edit.Positive.Connection.Node);
        SwarmMiniMaxH3ImageCollectReferencesNode collector =
            Assert.IsType<SwarmMiniMaxH3ImageCollectReferencesNode>(positive.MinimaxRefs.Connection.Node);
        Assert.Equal(2, collector.RefImages.Count);
        IVaeDecode source = Assert.IsType<VAEDecodeTiledNode>(collector.RefImages[0].Connection.Node);
        Assert.Same(baseSampler.Outputs[0], source.Samples.Connection);
        Assert.IsType<LoadImageNode>(collector.RefImages[1].Connection.Node);
        Assert.Empty(bridge.Graph.NodesOfType<IVaeEncode>());
    }

    [Theory]
    [InlineData("dancing", 1)]
    [InlineData("dancing <edit>edit it <b2eimage[prompt0]>", 2)]
    public void MiniMax_pixel_upscale_encodes_the_refiner_image_for_edit_sampling(string prompt, int referenceCount)
    {
        WorkflowGenerator.WorkflowGenStep refiner = new(g =>
        {
            g.IsRefinerStage = true;
            JArray conditioning = ["6", 0];
            string sampler = g.CreateKSampler(g.CurrentModel.Path, conditioning, conditioning, g.CurrentMedia.Path,
                1, 10, 8, 10000, 2, false, true, id: "23");
            g.CurrentMedia = g.CurrentMedia.WithPath([sampler, 0]);
        }, 2);
        JObject workflow = Generate("Refiner", prompt, input =>
        {
            input.Set(T2IParamTypes.RefinerMethod, "PostApply");
            input.Set(T2IParamTypes.RefinerControl, 0.2);
            input.Set(Base2EditExtension.KeepPreEditImage, true);
            input.Set(Base2EditExtension.EditUpscale, 2.0);
            input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
            input.Set(Base2EditExtension.EditSteps, 6);
            input.Set(Base2EditExtension.EditControl, 0.9);
            input.Set(T2IParamTypes.Width, 1664);
            input.Set(T2IParamTypes.Height, 2432);
            input.Set(T2IParamTypes.VAETileSize, 1024);
            using MagickImage pixels = new(MagickColors.Black, 16, 16);
            input.Set(T2IParamTypes.PromptImages, new List<Image> { new(pixels) });
        }, miniMaxBase: true, extraSteps: [refiner]);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmKSamplerNode edit = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>(), sampler => sampler.Id is not ("10" or "23"));
        Assert.Equal(1, edit.StartAtStep.LiteralAsInt());
        IVaeEncode encoded = Assert.IsType<VAEEncodeTiledNode>(edit.LatentImage.Connection.Node);
        ImageScaleNode scale = Assert.IsType<ImageScaleNode>(encoded.Pixels.Connection.Node);
        Assert.Equal(3328, scale.Width.LiteralAsInt());
        Assert.Equal(4864, scale.Height.LiteralAsInt());
        IVaeDecode source = Assert.IsType<VAEDecodeTiledNode>(scale.Image.Connection.Node);
        Assert.Equal("23", source.Samples.Connection.Node.Id);
        SwarmTextEncodeAdvancedNode positive = Assert.IsType<SwarmTextEncodeAdvancedNode>(edit.Positive.Connection.Node);
        SwarmMiniMaxH3ImageCollectReferencesNode collector =
            Assert.IsType<SwarmMiniMaxH3ImageCollectReferencesNode>(positive.MinimaxRefs.Connection.Node);
        Assert.Equal(referenceCount, collector.RefImages.Count);
        Assert.Same(encoded.Pixels.Connection, collector.RefImages[0].Connection);
        Assert.Same(encoded.Vae.Connection, collector.Vae.Connection);
        if (referenceCount == 2)
        {
            Assert.IsType<LoadImageNode>(collector.RefImages[1].Connection.Node);
        }
        Assert.Equal(scale.Width.LiteralAsInt(), positive.TargetWidth.LiteralAsInt());
        Assert.Equal(scale.Height.LiteralAsInt(), positive.TargetHeight.LiteralAsInt());
        SaveImageNode save = Assert.IsType<SaveImageNode>(bridge.Graph.GetNode("9"));
        IVaeDecode output = Assert.IsType<VAEDecodeTiledNode>(save.ImagesInput.Connection.Node);
        Assert.Same(edit.Outputs[0], output.Samples.Connection);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MiniMax_latent_upscale_conditions_and_samples_at_the_upscaled_latent_size(bool miniMaxBase)
    {
        JObject workflow = Generate("Base", "base <edit>edit it", input =>
        {
            input.Set(Base2EditExtension.EditUpscale, 1.25);
            input.Set(Base2EditExtension.EditUpscaleMethod, "latent-bilinear");
        }, miniMaxBase: miniMaxBase);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode edit = miniMaxBase
            ? Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>(), sampler => sampler.Id != "10")
            : GetEditSampler(bridge);
        INodeOutput samples = edit.FindInput("latent_image").Connection;
        LatentUpscaleByNode upscale = Assert.IsType<LatentUpscaleByNode>(samples.Node);
        SwarmTextEncodeAdvancedNode positive = Assert.IsType<SwarmTextEncodeAdvancedNode>(edit.FindInput("positive").Connection.Node);
        Assert.Equal(1040, positive.TargetWidth.LiteralAsInt());
        Assert.Equal(1520, positive.TargetHeight.LiteralAsInt());
        SwarmMiniMaxH3ImageCollectReferencesNode collector =
            Assert.IsType<SwarmMiniMaxH3ImageCollectReferencesNode>(positive.MinimaxRefs.Connection.Node);
        IVaeDecode source = Assert.IsAssignableFrom<IVaeDecode>(Assert.Single(collector.RefImages.Items).Connection.Node);
        Assert.Same(samples, source.Samples.Connection);
        Assert.Same(collector.Vae.Connection, source.Vae.Connection);
        if (miniMaxBase)
        {
            Assert.Equal("10", upscale.Samples.Connection.Node.Id);
        }
        else
        {
            IVaeEncode encoded = Assert.IsType<VAEEncodeNode>(upscale.Samples.Connection.Node);
            Assert.Same(collector.Vae.Connection, encoded.Vae.Connection);
            IVaeDecode baseDecode = Assert.IsAssignableFrom<IVaeDecode>(encoded.Pixels.Connection.Node);
            Assert.Equal("10", baseDecode.Samples.Connection.Node.Id);
            Assert.Equal("4", baseDecode.Vae.Connection.Node.Id);
        }
    }

    [Theory]
    [InlineData("Base", false)]
    [InlineData("Refiner", true)]
    public void Krea2_to_MiniMax_encodes_the_base_image_and_uses_it_as_the_first_reference(string applyAfter, bool tiled)
    {
        JObject workflow = Generate(applyAfter, "base <edit>edit it <b2eimage[base]>", input =>
        {
            // The saved metadata can still name the base model. The selected edit model drives generation.
            input.Set(Base2EditExtension.EditModelResolvedForMetadata, input.Get(T2IParamTypes.Model));
            input.Set(Base2EditExtension.KeepPreEditImage, true);
            input.Set(Base2EditExtension.EditSteps, 6);
            input.Set(Base2EditExtension.EditControl, 0.8);
            if (tiled)
            {
                input.Set(T2IParamTypes.VAETileSize, 512);
            }
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        KSamplerAdvancedNode edit = GetEditSampler(bridge);
        SwarmTextEncodeAdvancedNode positive = Assert.IsType<SwarmTextEncodeAdvancedNode>(edit.Positive.Connection.Node);
        SwarmTextEncodeAdvancedNode negative = Assert.IsType<SwarmTextEncodeAdvancedNode>(edit.Negative.Connection.Node);
        Assert.Equal("edit it", positive.Prompt.LiteralAsString()?.Trim());
        Assert.Null(negative.MinimaxRefs.Connection);
        Assert.Equal(832, positive.TargetWidth.LiteralAsInt());
        Assert.Equal(1216, positive.TargetHeight.LiteralAsInt());
        Assert.Equal(1, edit.StartAtStep.LiteralAsInt());

        SwarmMiniMaxH3ImageCollectReferencesNode collector =
            Assert.IsType<SwarmMiniMaxH3ImageCollectReferencesNode>(positive.MinimaxRefs.Connection.Node);
        INodeInput anchor = Assert.Single(collector.RefImages.Items);
        IVaeDecode source = Assert.IsAssignableFrom<IVaeDecode>(anchor.Connection.Node);
        Assert.Equal(tiled ? "VAEDecodeTiled" : "VAEDecode", anchor.Connection.Node.ClassTypeName);
        Assert.Equal("10", source.Samples.Connection.Node.Id);
        Assert.Equal("4", source.Vae.Connection.Node.Id);
        IVaeEncode encoded = Assert.Single(bridge.Graph.NodesOfType<IVaeEncode>());
        Assert.Equal(tiled ? "VAEEncodeTiled" : "VAEEncode", edit.LatentImage.Connection.Node.ClassTypeName);
        Assert.Same(encoded.LATENT, edit.LatentImage.Connection);
        Assert.Same(anchor.Connection, encoded.Pixels.Connection);
        Assert.Same(collector.Vae.Connection, encoded.Vae.Connection);
        AssertSavedEdit(bridge, edit, collector);
        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
    }

    [Fact]
    public void Krea2_to_MiniMax_pixel_upscale_encodes_the_scaled_image_and_keeps_reference_order()
    {
        JObject workflow = Generate("Refiner", "base <edit>edit it <b2eimage[prompt0]> <b2eimage[base]>", input =>
        {
            input.Set(Base2EditExtension.EditUpscale, 1.1);
            input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
            input.Set(T2IParamTypes.BatchSize, 3);
            using MagickImage pixels = new(MagickColors.Black, 16, 16);
            input.Set(T2IParamTypes.PromptImages, new List<Image> { new(pixels) });
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        KSamplerAdvancedNode edit = GetEditSampler(bridge);
        SwarmTextEncodeAdvancedNode positive = Assert.IsType<SwarmTextEncodeAdvancedNode>(edit.Positive.Connection.Node);
        SwarmMiniMaxH3ImageCollectReferencesNode collector =
            Assert.IsType<SwarmMiniMaxH3ImageCollectReferencesNode>(positive.MinimaxRefs.Connection.Node);
        Assert.Equal(3, collector.RefImages.Count);
        ImageScaleNode scale = Assert.IsType<ImageScaleNode>(collector.RefImages[0].Connection.Node);
        Assert.IsType<LoadImageNode>(collector.RefImages[1].Connection.Node);
        Assert.Same(scale.Image.Connection, collector.RefImages[2].Connection);
        IVaeEncode encoded = Assert.IsType<VAEEncodeNode>(edit.LatentImage.Connection.Node);
        Assert.Same(scale.IMAGE, encoded.Pixels.Connection);
        Assert.Same(collector.Vae.Connection, encoded.Vae.Connection);
        Assert.Equal(896, positive.TargetWidth.LiteralAsInt());
        Assert.Equal(1312, positive.TargetHeight.LiteralAsInt());
        Assert.Equal(scale.Width.LiteralAsInt(), positive.TargetWidth.LiteralAsInt());
        Assert.Equal(scale.Height.LiteralAsInt(), positive.TargetHeight.LiteralAsInt());
        AssertSavedEdit(bridge, edit, collector);
    }

    [Fact]
    public void MiniMax_encodes_the_final_image_after_upstream_pixel_processing()
    {
        WorkflowGenerator.WorkflowGenStep postprocess = new(g =>
        {
            WGNodeData decoded = g.CurrentMedia.AsRawImage(g.CurrentVae);
            string scaled = g.CreateNode("ImageScale", new JObject
            {
                ["image"] = decoded.Path,
                ["width"] = 1000,
                ["height"] = 1480,
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            });
            g.CurrentMedia = decoded.WithPath([scaled, 0]);
            g.CurrentMedia.Width = 1000;
            g.CurrentMedia.Height = 1480;
        }, 4);
        JObject workflow = Generate("Refiner", "dancing", input =>
        {
            input.Set(T2IParamTypes.RefinerMethod, "PostApply");
            input.Set(T2IParamTypes.RefinerControl, 0.2);
        }, extraSteps: [postprocess]);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        KSamplerAdvancedNode edit = GetEditSampler(bridge);
        IVaeEncode encoded = Assert.Single(bridge.Graph.NodesOfType<IVaeEncode>());
        ImageScaleNode scaled = Assert.IsType<ImageScaleNode>(encoded.Pixels.Connection.Node);
        IVaeDecode source = Assert.IsAssignableFrom<IVaeDecode>(scaled.Image.Connection.Node);
        Assert.Equal("10", source.Samples.Connection.Node.Id);
        Assert.Same(encoded.LATENT, edit.LatentImage.Connection);
        SwarmTextEncodeAdvancedNode positive = Assert.IsType<SwarmTextEncodeAdvancedNode>(edit.Positive.Connection.Node);
        SwarmMiniMaxH3ImageCollectReferencesNode collector =
            Assert.IsType<SwarmMiniMaxH3ImageCollectReferencesNode>(positive.MinimaxRefs.Connection.Node);
        Assert.Same(scaled.IMAGE, Assert.Single(collector.RefImages.Items).Connection);
        Assert.Same(collector.Vae.Connection, encoded.Vae.Connection);
        Assert.Equal(992, positive.TargetWidth.LiteralAsInt());
        Assert.Equal(1472, positive.TargetHeight.LiteralAsInt());
        AssertSavedEdit(bridge, edit, collector);
    }

    [Fact]
    public void Refine_only_keeps_encoded_sampler_input_and_ignores_references()
    {
        JObject workflow = Generate("Base", "base <edit>refine it <b2eimage[base]>",
            input => input.Set(Base2EditExtension.EditRefineOnly, true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        KSamplerAdvancedNode edit = GetEditSampler(bridge);
        IVaeEncode encoded = Assert.IsAssignableFrom<IVaeEncode>(edit.LatentImage.Connection.Node);
        IVaeDecode source = Assert.IsAssignableFrom<IVaeDecode>(encoded.Pixels.Connection.Node);
        Assert.Equal("10", source.Samples.Connection.Node.Id);
        Assert.Equal(1, edit.StartAtStep.LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmMiniMaxH3ImageCollectReferencesNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
    }

    [Fact]
    public void Upscale_only_saves_scaled_pixels_without_a_MiniMax_sampler()
    {
        JObject workflow = Generate("Refiner", "base <edit>upscale it", input =>
        {
            input.Set(Base2EditExtension.EditControl, 0.0);
            input.Set(Base2EditExtension.EditUpscale, 1.5);
            input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        Assert.Equal("10", Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>()).Id);
        Assert.Empty(bridge.Graph.NodesOfType<SwarmMiniMaxH3ImageCollectReferencesNode>());
        SaveImageNode save = Assert.IsType<SaveImageNode>(bridge.Graph.GetNode("9"));
        ImageScaleNode upscale = Assert.IsType<ImageScaleNode>(save.ImagesInput.Connection.Node);
        Assert.Equal(1248, upscale.Width.LiteralAsInt());
        Assert.Equal(1824, upscale.Height.LiteralAsInt());
    }

    private static KSamplerAdvancedNode GetEditSampler(WorkflowBridge bridge) =>
        Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>(), sampler => sampler.Id != "10");

    private static void AssertSavedEdit(WorkflowBridge bridge, KSamplerAdvancedNode edit, SwarmMiniMaxH3ImageCollectReferencesNode collector)
    {
        SaveImageNode save = Assert.IsType<SaveImageNode>(bridge.Graph.GetNode("9"));
        IVaeDecode output = Assert.IsAssignableFrom<IVaeDecode>(save.ImagesInput.Connection.Node);
        Assert.Same(edit.Outputs[0], output.Samples.Connection);
        Assert.Same(collector.Vae.Connection, output.Vae.Connection);
        VAELoaderNode vae = Assert.IsType<VAELoaderNode>(collector.Vae.Connection.Node);
        Assert.Equal("MiniMaxVae.safetensors", vae.VaeName.LiteralAsString());
        CLIPLoaderNode clip = Assert.IsType<CLIPLoaderNode>(
            Assert.IsType<SwarmTextEncodeAdvancedNode>(edit.Positive.Connection.Node).Clip.Connection.Node);
        Assert.Equal("minimax", clip.Type.LiteralAsString());
        Assert.Contains(EditModelName, edit.Model.Connection.Node.Inputs.Select(input => input.LiteralAsString()));
    }

    private static JObject Generate(string applyAfter, string prompt, Action<T2IParamInput> configure,
        bool miniMaxBase = false, params WorkflowGenerator.WorkflowGenStep[] extraSteps)
    {
        using SwarmUiTestContext context = new();
        WorkflowTestHarness.Base2EditSteps();
        T2IModelHandler models = new() { ModelType = "Stable-Diffusion" };
        T2IModel baseModel = new(models, "/tmp", "/tmp/Krea2.safetensors", "Krea2.safetensors")
        {
            ModelClass = new() { ID = "krea-2", CompatClass = T2IModelClassSorter.CompatKrea2 }
        };
        T2IModel editModel = new(models, "/tmp", $"/tmp/{EditModelName}", EditModelName)
        {
            ModelClass = new() { ID = "minimax-h3-image", CompatClass = T2IModelClassSorter.CompatMiniMaxH3Image }
        };
        if (miniMaxBase)
        {
            baseModel = editModel;
        }
        models.Models[baseModel.Name] = baseModel;
        models.Models[editModel.Name] = editModel;
        T2IModelHandler clips = new() { ModelType = "Clip" };
        T2IModelHandler vaes = new() { ModelType = "VAE" };
        T2IModel vae = new(vaes, "/tmp", "/tmp/MiniMaxVae.safetensors", "MiniMaxVae.safetensors")
        {
            ModelClass = new() { ID = "minimax-h3-image/vae", CompatClass = T2IModelClassSorter.CompatMiniMaxH3Image }
        };
        vaes.Models[vae.Name] = vae;
        Program.T2IModelSets = new() { ["Stable-Diffusion"] = models, ["VAE"] = vaes };

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.QwenModel, new T2IModel(clips, "/tmp", "/tmp/MiniMaxClip.safetensors", "MiniMaxClip.safetensors"));
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 832);
        input.Set(T2IParamTypes.Height, 1216);
        input.Set(Base2EditExtension.EditModel, editModel.Name);
        input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        input.Set(Base2EditExtension.EditSteps, 20);
        input.Set(Base2EditExtension.EditControl, 0.95);
        configure(input);

        WorkflowGenerator.WorkflowGenStep baseSampler = new(g =>
        {
            if (miniMaxBase)
            {
                g.Features.Add("variation_seed");
                g.CurrentVae = new WGNodeData(g.CreateVAELoader(vae.Name), g, WGNodeData.DT_VAE, g.CurrentCompat());
            }
            // The upload is an explicit edit reference, outside this base-generation fixture.
            using ParamSnapshot editUploads = ParamSnapshot.Of(g.UserInput, T2IParamTypes.PromptImages.Type);
            editUploads.Remove();
            WGNodeData latent = g.EmptyImage(g.UserInput.GetImageWidth(), g.UserInput.GetImageHeight(),
                g.UserInput.Get(T2IParamTypes.BatchSize, 1), "5");
            JArray conditioning = [g.CreateNode("CLIPTextEncode", new JObject
            {
                ["clip"] = g.CurrentTextEnc.Path, ["text"] = "base"
            }, "6"), 0];
            g.CreateKSampler(g.CurrentModel.Path, conditioning, conditioning, latent.Path, 4, 20, 0, 10000, 1, false, true, id: "10");
            g.CurrentMedia = latent.WithPath(["10", 0]);
        }, -900);
        WorkflowGenerator.WorkflowGenStep save = new(g =>
        {
            g.CurrentMedia = g.CurrentMedia.DecodeLatents(g.CurrentVae, false, "8");
            g.CreateNode("SaveImage", new JObject { ["images"] = g.CurrentMedia.Path, ["filename_prefix"] = "test" }, "9");
        }, 5);

        bool addedVae = CommonModels.Known.TryAdd(EditVaeId, new(EditVaeId, "MiniMax VAE", "", "", "", "VAE", vae.Name));
        try
        {
            return WorkflowTestHarness.GenerateWithSteps(input, WorkflowTestHarness.Template_BaseOnlyLatents()
                .Append(baseSampler).Append(save).Concat(extraSteps).Concat(WorkflowTestHarness.Base2EditSteps()));
        }
        finally
        {
            if (addedVae)
            {
                CommonModels.Known.TryRemove(EditVaeId, out _);
            }
        }
    }
}
