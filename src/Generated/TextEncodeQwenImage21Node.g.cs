using ComfyTyped.Core;
using ComfyTyped.Types;

namespace Base2Edit.Generated;

/// <summary>Typed binding for ComfyUI's native Qwen Image 2.1 conditioning node.</summary>
public sealed class TextEncodeQwenImage21Node : ComfyNode
{
    public const string ClassType = "TextEncodeQwenImage21";
    public override string ClassTypeName => ClassType;

    public NodeOutput<ConditioningType> Positive { get; }
    public NodeOutput<ConditioningType> Negative { get; }
    public NodeOutput<LatentType> Latent { get; }
    public NodeInput<ClipType> Clip { get; }
    public NodeInput<StringType> Prompt { get; }
    public NodeInput<StringType> NegativePrompt { get; }
    public NodeInput<VaeType> Vae { get; }
    public NodeInput<IntType> Resolution { get; }
    public NodeInputList<ImageType> Images { get; }

    public TextEncodeQwenImage21Node()
    {
        Positive = AddOutput<ConditioningType>(0, "positive");
        Negative = AddOutput<ConditioningType>(1, "negative");
        Latent = AddOutput<LatentType>(2, "latent");
        Clip = AddInput<ClipType>("clip");
        Prompt = AddInput<StringType>("prompt");
        NegativePrompt = AddInput<StringType>("negative_prompt");
        Vae = AddInput<VaeType>("vae");
        Resolution = AddInput<IntType>("resolution");
        Resolution.Set(1024L);
        Images = AddInputList<ImageType>("images",
            names: Enumerable.Range(1, 16).Select(i => $"image_{i}").ToArray(), max: 16);
    }
}
