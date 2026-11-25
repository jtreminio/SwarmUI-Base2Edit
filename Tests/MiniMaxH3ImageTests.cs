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
    [InlineData("Base", false)]
    [InlineData("Refiner", true)]
    public void Krea2_to_MiniMax_uses_native_references_and_saves_the_edit(string applyAfter, bool tiled)
    {
        JObject workflow = Generate(applyAfter, "base <edit>edit it <b2eimage[base]>", input =>
        {
            // The saved metadata can still name the base model. The selected edit model drives generation.
            input.Set(Base2EditExtension.EditModelResolvedForMetadata, input.Get(T2IParamTypes.Model));
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
        AssertSavedEdit(bridge, edit, collector);
        AssertEmptyLatent(workflow, edit, 832, 1216, 1);
        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());
        Assert.Empty(bridge.Graph.NodesOfType<IVaeEncode>());
    }

    [Fact]
    public void Pixel_upscale_keeps_stage_image_first_and_matches_empty_latent_to_stage_size_and_batch()
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
        Assert.Equal(896, positive.TargetWidth.LiteralAsInt());
        Assert.Equal(1312, positive.TargetHeight.LiteralAsInt());
        AssertSavedEdit(bridge, edit, collector);
        AssertEmptyLatent(workflow, edit, 896, 1312, 3);
        Assert.Empty(bridge.Graph.NodesOfType<IVaeEncode>());
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

    private static void AssertEmptyLatent(JObject workflow, KSamplerAdvancedNode edit, int width, int height, int batch)
    {
        JToken latent = workflow[edit.LatentImage.Connection.Node.Id];
        Assert.Equal("SwarmEmptyMiniMaxH3ImageLatent", latent["class_type"].Value<string>());
        Assert.Equal(width, latent["inputs"]["width"].Value<int>());
        Assert.Equal(height, latent["inputs"]["height"].Value<int>());
        Assert.Equal(batch, latent["inputs"]["batch_size"].Value<int>());
    }

    private static JObject Generate(string applyAfter, string prompt, Action<T2IParamInput> configure)
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
            // The upload is an explicit edit reference, outside this base-generation fixture.
            using ParamSnapshot editUploads = ParamSnapshot.Of(g.UserInput, T2IParamTypes.PromptImages.Type);
            editUploads.Remove();
            WGNodeData latent = g.EmptyImage(832, 1216, g.UserInput.Get(T2IParamTypes.BatchSize, 1), "5");
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
                .Append(baseSampler).Append(save).Concat(WorkflowTestHarness.Base2EditSteps()));
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
