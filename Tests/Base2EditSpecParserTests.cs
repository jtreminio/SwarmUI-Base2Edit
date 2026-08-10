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
    public void Stage0_blankEditModel_throws(string editModel)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, editModel);

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() => ParseStage0(input));
        Assert.Contains("missing required field 'Model'", ex.Message);
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
    public void EditStage_emptyJsonSampler_isNull()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Base\",\"Model\":\"" + ModelPrep.UseBase + "\",\"Sampler\":\"\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Null(stage1.Sampler);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"\\t  \"")]
    public void EditStage_blankJsonScheduler_isNull(string schedulerJson)
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            $"[{{\"ApplyAfter\":\"Base\",\"Model\":\"{ModelPrep.UseBase}\",\"Scheduler\":{schedulerJson}}}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Null(stage1.Scheduler);
    }

    [Fact]
    public void EditStage_emptyJsonVae_isNull()
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
            "[{\"ApplyAfter\":\"Base\",\"Model\":\"" + ModelPrep.UseBase + "\",\"Vae\":\"\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Null(stage1.Vae);
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
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
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

    [Fact]
    public void EditStage_omitsCfgScale_getsMainCFG_notInheritedFromParent()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(T2IParamTypes.CFGScale, 6.0);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditCFGScale, 3.5);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage0 = stages.Single(s => s.Id == 0);
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal(3.5, stage0.CfgScale);
        Assert.Equal(6.0, stage1.CfgScale);
    }

    [Fact]
    public void EditStage_omitsSampler_fieldIsNull_notInheritedFromParent()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditSampler, "dpmpp_2m");
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage0 = stages.Single(s => s.Id == 0);
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal("dpmpp_2m", stage0.Sampler);
        Assert.Null(stage1.Sampler);
    }

    [Fact]
    public void EditStage_omitsScheduler_fieldIsNull_notInheritedFromParent()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditScheduler, "karras");
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage0 = stages.Single(s => s.Id == 0);
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal("karras", stage0.Scheduler);
        Assert.Null(stage1.Scheduler);
    }

    [Fact]
    public void EditStage_omitsControl_getsDefaultOne_notInheritedFromParent()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditControl, 0.5);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage0 = stages.Single(s => s.Id == 0);
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal(0.5, stage0.Control);
        Assert.Equal(1.0, stage1.Control);
    }

    [Fact]
    public void EditStage_omitsUpscale_getsDefaultOne_notInheritedFromParent()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditUpscale, 2.0);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage0 = stages.Single(s => s.Id == 0);
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal(2.0, stage0.Upscale);
        Assert.Equal(1.0, stage1.Upscale);
    }

    [Fact]
    public void EditStage_omitsUpscaleMethod_getsPixelLanczos_notInheritedFromParent()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditUpscaleMethod, "nearest-exact");
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage0 = stages.Single(s => s.Id == 0);
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal("nearest-exact", stage0.UpscaleMethod);
        Assert.Equal("pixel-lanczos", stage1.UpscaleMethod);
    }

    [Fact]
    public void EditStage_omitsSteps_getsDefaultTwenty_notInheritedFromParent()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditSteps, 50);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage0 = stages.Single(s => s.Id == 0);
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal(50, stage0.Steps);
        Assert.Equal(20, stage1.Steps);
    }

    [Fact]
    public void EditStage_omitsVae_fieldIsNull_notInheritedFromParent()
    {
        using SwarmUiTestContext _ = new();
        T2IModelHandler vaeHandler = new() { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["VAE"] = vaeHandler
        };
        T2IModel parentVae = new(vaeHandler, "/tmp", "/tmp/Parent_Vae.safetensors", "Parent_Vae.safetensors");
        vaeHandler.Models[parentVae.Name] = parentVae;

        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditVAE, parentVae);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Null(stage1.Vae);
    }

    [Fact]
    public void EditStage_omitsModel_throws()
    {
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Steps\":10}]");

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => Base2EditSpecParser.Parse(MakeGenerator(input)));
        Assert.Contains("missing required field 'Model'", ex.Message);
    }

    [Fact]
    public void Stage0_applyAfterSeedVR2_whenSeedVR2Enabled_isSeedVR2Kind()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureSeedVR2ModelStubRegistered();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.ApplyEditAfter, "SeedVR2");
        input.Set(UnitTestStubs.SeedVR2ModelStub, "seedvr2-auto");

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal(ParentKind.SeedVR2, stage0.ParentKind);
    }

    [Fact]
    public void Stage0_applyAfterSeedVR2_whenSeedVR2Disabled_downgradesToBase()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureSeedVR2ModelStubRegistered();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.ApplyEditAfter, "SeedVR2");
        // SeedVR2 model value intentionally unset -> group disabled -> downgrade SeedVR2 -> Refiner -> Base.

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal(ParentKind.Base, stage0.ParentKind);
    }

    [Fact]
    public void EditStage_applyAfterSeedVR2_whenSeedVR2Enabled_isSeedVR2Kind()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureSeedVR2ModelStubRegistered();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(UnitTestStubs.SeedVR2ModelStub, "seedvr2-auto");
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"SeedVR2\",\"Model\":\"" + ModelPrep.UseBase + "\"}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal(ParentKind.SeedVR2, stage1.ParentKind);
    }

    [Fact]
    public void Stage0_applyAfterSeedVR2_videoGenerationAfterVideo_downgrades()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureSeedVR2ModelStubRegistered();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.ApplyEditAfter, "SeedVR2");
        input.Set(UnitTestStubs.SeedVR2ModelStub, "seedvr2-auto");
        // Video generation with the default "after_video" upscale stage: the sister's priority-6
        // image upscale does not run (it defers to priority 15), so the SeedVR2 anchor must
        // downgrade through Refiner -> Base (no refiner configured here).
        input.Set(T2IParamTypes.Prompt, "test <extend:1>");

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal(ParentKind.Base, stage0.ParentKind);
    }

    [Fact]
    public void Stage0_rootEditControlZero_isAccepted()
    {
        // Edit Control's Min was relaxed from 0.1 to 0; the parser/root path must accept
        // and preserve 0 rather than clamping it back up.
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditControl, 0.0);

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal(0.0, stage0.Control);
    }

    [Fact]
    public void EditStage_jsonControlZero_isAccepted_notNormalizedUp()
    {
        // A stage card with Control 0 must survive NormalizeControl's Clamp(control, 0, 1)
        // as exactly 0, not get pulled up to some minimum.
        using SwarmUiTestContext _ = new();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.EditStages,
            "[{\"ApplyAfter\":\"Edit Stage 0\",\"Model\":\"" + ModelPrep.UseBase + "\",\"Control\":0}]");

        List<StageSpec> stages = Base2EditSpecParser.Parse(MakeGenerator(input));
        StageSpec stage1 = stages.Single(s => s.Id == 1);

        Assert.Equal(0.0, stage1.Control);
    }
}
