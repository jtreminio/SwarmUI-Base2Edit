using Base2Edit;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class EditPromptLoraParsingTests
{
    [Fact]
    public void ParseEditPromptLoras_returns_matched_loras_with_weights()
    {
        string[] available = ["MyLora.safetensors"];
        var (loras, weights, tencWeights) = LoraParsing.ParseEditPromptLoras(
            "test <lora:MyLora:2> more",
            available
        );

        Assert.Equal(["MyLora"], loras);
        Assert.Equal(["2"], weights);
        Assert.Equal(["2"], tencWeights);
    }

    [Fact]
    public void ParseEditPromptLoras_supports_multiple_and_tenc_weights()
    {
        string[] available = ["Foo.safetensors", "Bar"];
        var (loras, weights, tencWeights) = LoraParsing.ParseEditPromptLoras(
            "a <lora:Foo:2:3> b <lora:Bar:4>",
            available
        );

        Assert.Equal(["Foo", "Bar"], loras);
        Assert.Equal(["2", "4"], weights);
        Assert.Equal(["3", "4"], tencWeights);
    }

    [Fact]
    public void ParseEditPromptLoras_dedupes_by_model_name()
    {
        string[] available = ["dup_lora.safetensors"];
        var (loras, weights, tencWeights) = LoraParsing.ParseEditPromptLoras(
            "<lora:dup_lora:1> <lora:Dup_Lora:2>",
            available
        );

        Assert.Single(loras);
        Assert.Equal("dup_lora", loras[0]);
        Assert.Single(weights);
        Assert.Equal("1", weights[0]);
        Assert.Single(tencWeights);
        Assert.Equal("1", tencWeights[0]);
    }

    [Fact]
    public void ParseEditPromptLoras_skips_missing_matches()
    {
        string[] available = ["Known"];
        var (loras, weights, tencWeights) = LoraParsing.ParseEditPromptLoras(
            "<lora:Missing:2>",
            available
        );

        Assert.Empty(loras);
        Assert.Empty(weights);
        Assert.Empty(tencWeights);
    }
}
