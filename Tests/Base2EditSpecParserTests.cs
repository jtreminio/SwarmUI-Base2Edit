using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class Base2EditSpecParserTests
{
    private static T2IParamInput BuildBaseInput()
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "test");
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        return input;
    }

    private static WorkflowGenerator MakeGenerator(T2IParamInput input) => new()
    {
        UserInput = input,
        Features = [],
        ModelFolderFormat = "/"
    };

    private static StageSpec ParseStage0(T2IParamInput input)
    {
        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        return stages.Single(s => s.Id == 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Stage0_blankEditModel_fallsBackToInheritedModel(string editModel)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, editModel);

        StageSpec stage0 = ParseStage0(input);

        Assert.Null(stage0.Model);
        Assert.Equal(ModelSource.Base, stage0.ModelSource);
    }

    [Fact]
    public void Stage0_useBaseSentinel_resolvesToBaseSource()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);

        StageSpec stage0 = ParseStage0(input);

        Assert.Null(stage0.Model);
        Assert.Equal(ModelSource.Base, stage0.ModelSource);
    }

    [Fact]
    public void Stage0_unknownModelName_throws()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, "MyCustomModel.safetensors");

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() => ParseStage0(input));
        Assert.Contains("unknown Model", ex.Message);
    }

    [Fact]
    public void EditStage_emptyJsonSampler_fallsBackToParamDefaultSampler()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Base\",\"Sampler\":\"\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal("euler", stage1.Sampler);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"\\t  \"")]
    public void EditStage_blankJsonScheduler_fallsBackToParamDefaultScheduler(string schedulerJson)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            $"[{{\"ApplyAfter\":\"Base\",\"Scheduler\":{schedulerJson}}}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal("normal", stage1.Scheduler);
    }

    [Fact]
    public void EditStage_emptyJsonVae_fallsBackToInheritedVae()
    {
        using SwarmUiTestContext _ = new();
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["VAE"] = vaeHandler
        };
        T2IModel inheritedVae = new(vaeHandler, "/tmp", "/tmp/Inherited_Vae.safetensors", "Inherited_Vae.safetensors");
        vaeHandler.Models[inheritedVae.Name] = inheritedVae;

        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(T2IParamTypes.VAE, inheritedVae);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Base\",\"Vae\":\"\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal(inheritedVae, stage1.Vae);
    }

    [Fact]
    public void Stage0_editVaeWithRealName_resolvesToT2IModel()
    {
        using SwarmUiTestContext _ = new();
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["VAE"] = vaeHandler
        };
        T2IModel realVae = new(vaeHandler, "/tmp", "/tmp/UnitTest_Vae.safetensors", "UnitTest_Vae.safetensors");
        vaeHandler.Models[realVae.Name] = realVae;

        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditVAE, realVae);

        StageSpec stage0 = ParseStage0(input);

        Assert.NotNull(stage0.Vae);
        Assert.Equal("UnitTest_Vae.safetensors", stage0.Vae.Name);
    }

    [Fact]
    public void InvalidApplyAfter_throws()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"nonsense\"}]");

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => Base2EditSpecParser.Parse(MakeGenerator(input)));
        Assert.Contains("invalid Apply After", ex.Message);
    }

    [Fact]
    public void ApplyAfterReferencingNonEarlierStage_throws()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 5\"}]");

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => Base2EditSpecParser.Parse(MakeGenerator(input)));
        Assert.Contains("must reference an earlier stage", ex.Message);
    }

    [Fact]
    public void ApplyAfterReferencingMissingStage_throws()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            "[{\"Skipped\":\"true\"},{\"ApplyAfter\":\"Edit Stage 1\"}]");

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => Base2EditSpecParser.Parse(MakeGenerator(input)));
        Assert.Contains("missing or invalid", ex.Message);
    }
}
