using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

/// <summary>
/// Focused unit tests for behavior changes introduced by the parser refactor that unified
/// stage-0 (from UserInput) and JSON-stage parsing under a single <c>ParseStage</c>.
///
/// The original <c>ParseStage0</c> used <c>editValue ?? inherited.X</c> for resolution,
/// so an explicitly empty user value (e.g. <c>EditModel = ""</c>) propagated as <c>""</c>
/// into the resolved <see cref="StageSpec"/>. The new shim path drops empty/whitespace
/// values before they enter the JObject, so the resolved field uses the inherited default.
/// This is a deliberate improvement — empty was never a meaningful override — but the change
/// should be locked in by tests so a future revert is caught.
/// </summary>
[Collection("Base2EditTests")]
public class Base2EditSpecParserTests
{
    private static T2IParamInput BuildBaseInput()
    {
        _ = WorkflowTestHarness.Base2EditSteps();
        var input = new T2IParamInput(null);
        input.Set(T2IParamTypes.Prompt, "test");
        // Apply After Base + no refiner-phase work => hasRefinerPhaseWork is false and
        // stage 0 ApplyAfter resolves to Base, so inherited = baseDefaults (predictable).
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

    // ----- C: empty/whitespace string Edit* params fall back to inherited -----

    [Fact]
    public void Stage0_emptyEditModel_fallsBackToInheritedModel()
    {
        using var _ = new SwarmUiTestContext();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, "");

        StageSpec stage0 = ParseStage0(input);

        // baseDefaults.Model = T2IParamTypes.Model?.Name ?? ModelPrep.UseBase
        // No T2IParamTypes.Model set => UseBase. Original code would have produced "".
        Assert.Equal(ModelPrep.UseBase, stage0.Model);
    }

    [Fact]
    public void Stage0_whitespaceEditModel_fallsBackToInheritedModel()
    {
        using var _ = new SwarmUiTestContext();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, "   ");

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal(ModelPrep.UseBase, stage0.Model);
    }

    [Fact]
    public void Stage0_nonEmptyEditModel_overridesInherited()
    {
        // Sanity guardrail: a non-empty EditModel must still propagate through.
        using var _ = new SwarmUiTestContext();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditModel, "MyCustomModel.safetensors");

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal("MyCustomModel.safetensors", stage0.Model);
    }

    [Fact]
    public void Stage0_emptyEditSampler_fallsBackToInheritedSampler()
    {
        using var _ = new SwarmUiTestContext();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditSampler, "");

        StageSpec stage0 = ParseStage0(input);

        // paramDefaults.Sampler resolves to baseDefaults.Sampler (resolvedModel != UseRefiner).
        // baseDefaults.Sampler = ComfyUIBackendExtension.SamplerParam default = "euler".
        // Original code would have produced "" here.
        Assert.Equal("euler", stage0.Sampler);
    }

    [Fact]
    public void Stage0_emptyEditScheduler_fallsBackToInheritedScheduler()
    {
        using var _ = new SwarmUiTestContext();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditScheduler, "");

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal("normal", stage0.Scheduler);
    }

    [Fact]
    public void Stage0_whitespaceEditScheduler_fallsBackToInheritedScheduler()
    {
        using var _ = new SwarmUiTestContext();
        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditScheduler, "\t  ");

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal("normal", stage0.Scheduler);
    }

    // ----- D: EditVAE with empty Name resolves to inherited.Vae, HasVaeOverride=false -----

    [Fact]
    public void Stage0_editVaeWithEmptyName_fallsBackToInheritedAndNoOverride()
    {
        using var _ = new SwarmUiTestContext();
        var vaeHandler = new T2IModelHandler { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["VAE"] = vaeHandler
        };
        var emptyNameVae = new T2IModel(vaeHandler, "/tmp", "/tmp/empty.safetensors", "");
        vaeHandler.Models[emptyNameVae.Name] = emptyNameVae;

        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditVAE, emptyNameVae);

        StageSpec stage0 = ParseStage0(input);

        // baseDefaults.Vae = T2IParamTypes.VAE?.Name ?? "None". No VAE set => "None".
        // Original code: vaeModel?.Name ?? inherited => "" (not "None").
        // HasVaeOverride was already false in both paths; we lock that in too.
        Assert.Equal("None", stage0.Vae);
        Assert.False(stage0.HasVaeOverride);
    }

    [Fact]
    public void Stage0_editVaeWithRealName_setsHasVaeOverride()
    {
        // Sanity guardrail: a real VAE name must still set HasVaeOverride.
        using var _ = new SwarmUiTestContext();
        var vaeHandler = new T2IModelHandler { ModelType = "VAE" };
        Program.T2IModelSets = new Dictionary<string, T2IModelHandler>
        {
            ["VAE"] = vaeHandler
        };
        var realVae = new T2IModel(vaeHandler, "/tmp", "/tmp/UnitTest_Vae.safetensors", "UnitTest_Vae.safetensors");
        vaeHandler.Models[realVae.Name] = realVae;

        T2IParamInput input = BuildBaseInput();
        input.Set(Base2EditExtension.EditVAE, realVae);

        StageSpec stage0 = ParseStage0(input);

        Assert.Equal("UnitTest_Vae.safetensors", stage0.Vae);
        Assert.True(stage0.HasVaeOverride);
    }
}
