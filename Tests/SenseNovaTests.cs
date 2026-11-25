using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using ImageMagick;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class SenseNovaTests
{
    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public void Full_edit_uses_the_sensenova_checkpoint_and_resolves_ordered_pixel_references(string applyAfter)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("base <edit>make it blue <b2eimage[prompt0]> <b2eimage[base]>");
        input.Set(Base2EditExtension.ApplyEditAfter, applyAfter);
        using MagickImage upload = new(MagickColors.Black, 128, 96);
        input.Set(T2IParamTypes.PromptImages, new List<Image> { new(upload) });
        var (workflow, generator) = WorkflowTestHarness.GenerateWithStepsAndState(input,
            WorkflowTestHarness.Template_BaseOnlyLatents().Concat(WorkflowTestHarness.Base2EditSteps()));

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        CheckpointLoaderSimpleNode checkpoint = Assert.Single(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());
        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        Assert.Same(checkpoint.Outputs[0], sampler.Model.Connection);
        Assert.Equal(0L, sampler.StartAtStep.LiteralAsLong());
        Assert.Equal(50L, sampler.Steps.LiteralAsLong());
        Assert.Equal("euler", sampler.SamplerName.LiteralAsString());
        Assert.Equal("simple", sampler.Scheduler.LiteralAsString());
        EmptyHiDreamO1LatentImageNode empty = Assert.IsType<EmptyHiDreamO1LatentImageNode>(sampler.LatentImage.Connection.Node);
        Assert.Equal(800L, empty.Width.LiteralAsLong());
        Assert.Equal(1216L, empty.Height.LiteralAsLong());
        Assert.Equal(800, generator.CurrentMedia.Width);
        Assert.Equal(1216, generator.CurrentMedia.Height);

        SenseNovaConditioningNode conditioning = Assert.IsType<SenseNovaConditioningNode>(sampler.Positive.Connection.Node);
        Assert.Equal("image", conditioning.Task.LiteralAsString());
        Assert.Same(conditioning.Negative, sampler.Negative.Connection);
        Assert.Same(checkpoint.Outputs[1], conditioning.Clip.Connection);
        HiDreamO1ReferenceImagesNode references = Assert.IsType<HiDreamO1ReferenceImagesNode>(conditioning.ConditioningInput.Connection.Node);
        Assert.Same(references.Negative, conditioning.NegativeInput.Connection);
        CLIPTextEncodeNode positive = Assert.IsType<CLIPTextEncodeNode>(references.PositiveInput.Connection.Node);
        CLIPTextEncodeNode negative = Assert.IsType<CLIPTextEncodeNode>(references.NegativeInput.Connection.Node);
        Assert.Equal("make it blue", positive.Text.LiteralAsString().Trim());
        Assert.Equal("blurry", negative.Text.LiteralAsString());
        Assert.Same(checkpoint.Outputs[1], positive.Clip.Connection);
        Assert.Same(checkpoint.Outputs[1], negative.Clip.Connection);

        var images = references.Images.Items.Select(item => ReferenceSource(item.Connection)).ToArray();
        Assert.Equal(2, images.Length);
        Assert.IsType<LoadImageNode>(images[0]);
        IVaeDecode source = Assert.IsAssignableFrom<IVaeDecode>(images[1]);
        Assert.Equal("10", source.Samples.Connection.Node.Id);
        Assert.Equal("4", source.Vae.Connection.Node.Id);
        Assert.Empty(bridge.Graph.NodesOfType<IVaeEncode>());
        Assert.Empty(bridge.Graph.NodesOfType<ReferenceLatentNode>());

        IVaeDecode output = Assert.Single(bridge.Graph.NodesOfType<IVaeDecode>(),
            decoder => decoder.Samples.Connection == sampler.Outputs[0]);
        Assert.Same(checkpoint.Outputs[2], output.Vae.Connection);
    }

    [Theory]
    [InlineData(false, 0.5, 25)]
    [InlineData(true, 1.0, 0)]
    public void Partial_edits_and_refine_only_sample_from_the_aligned_source(bool refineOnly, double control, int startStep)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildInput("base <edit>make it blue <b2eimage[base]>");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(Base2EditExtension.EditControl, control);
        input.Set(Base2EditExtension.EditRefineOnly, refineOnly);
        input.Set(Base2EditExtension.EditUpscale, 1.25);
        input.Set(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input,
            WorkflowTestHarness.Template_EditOnly().Concat(WorkflowTestHarness.Base2EditSteps()));

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        KSamplerAdvancedNode sampler = Assert.Single(bridge.Graph.NodesOfType<KSamplerAdvancedNode>());
        Assert.Equal((long)startStep, sampler.StartAtStep.LiteralAsLong());
        IVaeEncode encode = Assert.IsAssignableFrom<IVaeEncode>(sampler.LatentImage.Connection.Node);
        CheckpointLoaderSimpleNode checkpoint = Assert.Single(bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>());
        Assert.Same(checkpoint.Outputs[2], encode.Vae.Connection);
        ImageScaleNode aligned = Assert.IsType<ImageScaleNode>(encode.Pixels.Connection.Node);
        Assert.Equal(992L, aligned.Width.LiteralAsLong());
        Assert.Equal(1504L, aligned.Height.LiteralAsLong());
        ImageScaleNode upscaled = Assert.IsType<ImageScaleNode>(
            Assert.IsType<SplitImageWithAlphaNode>(aligned.Image.Connection.Node).Image.Connection.Node);
        Assert.Equal("11", upscaled.Image.Connection.Node.Id);
        Assert.Empty(bridge.Graph.NodesOfType<EmptyHiDreamO1LatentImageNode>());

        if (refineOnly)
        {
            Assert.IsType<CLIPTextEncodeNode>(sampler.Positive.Connection.Node);
            Assert.Empty(bridge.Graph.NodesOfType<HiDreamO1ReferenceImagesNode>());
            Assert.Empty(bridge.Graph.NodesOfType<SenseNovaConditioningNode>());
        }
        else
        {
            HiDreamO1ReferenceImagesNode references = Assert.Single(bridge.Graph.NodesOfType<HiDreamO1ReferenceImagesNode>());
            Assert.Same(upscaled, ReferenceSource(references.Images.Items.Last().Connection));
        }
    }

    private static ComfyNode ReferenceSource(INodeOutput reference)
    {
        ImageScaleToTotalPixelsNode resize = Assert.IsType<ImageScaleToTotalPixelsNode>(reference.Node);
        Assert.Equal(4.0, resize.Megapixels.LiteralAsDouble());
        Assert.Equal(32L, resize.ResolutionSteps.LiteralAsLong());
        return Assert.IsType<SplitImageWithAlphaNode>(resize.Image.Connection.Node).Image.Connection.Node;
    }

    private static T2IParamInput BuildInput(string prompt)
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IModelHandler handler = new() { ModelType = "Stable-Diffusion" };
        const string editModelName = "sensenova_u1.6_8b_mot_int8_convrot.safetensors";
        T2IModel editModel = new(handler, "/tmp", $"/tmp/{editModelName}", editModelName)
        {
            ModelClass = new()
            {
                ID = "sensenova-u1.5",
                CompatClass = T2IModelClassSorter.CompatSenseNovaU15,
                StandardWidth = 2048,
                StandardHeight = 2048
            }
        };
        T2IModel baseModel = new(handler, "/tmp", "/tmp/SDXL.safetensors", "SDXL.safetensors")
        {
            ModelClass = new()
            {
                ID = "stable-diffusion-xl-v1/base",
                CompatClass = T2IModelClassSorter.CompatSdxl,
                StandardWidth = 1024,
                StandardHeight = 1024
            }
        };
        handler.Models[baseModel.Name] = baseModel;
        handler.Models[editModel.Name] = editModel;
        Program.T2IModelSets = new() { ["Stable-Diffusion"] = handler };

        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.NegativePrompt, "blurry");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 816);
        input.Set(T2IParamTypes.Height, 1216);
        input.Set(Base2EditExtension.EditModel, editModelName);
        input.Set(Base2EditExtension.EditSteps, 50);
        input.Set(Base2EditExtension.EditCFGScale, 4.0);
        return input;
    }
}
