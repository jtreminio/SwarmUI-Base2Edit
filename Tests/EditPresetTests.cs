using System.Runtime.CompilerServices;
using LiteDB;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public sealed class EditPresetTests : IDisposable
{
    private readonly SwarmUiTestContext _context = new();
    private readonly MemoryStream _stream = new();
    private readonly LiteDatabase _database;
    private readonly Session _session;
    private readonly T2IModel _baseModel;
    private readonly T2IModel _editModel;

    public EditPresetTests()
    {
        WorkflowTestHarness.Base2EditSteps();
        _database = new LiteDatabase(_stream);
        // Use an isolated in-memory preset store without opening the user's database.
        SessionHandler sessions = (SessionHandler)RuntimeHelpers.GetUninitializedObject(typeof(SessionHandler));
        sessions.DBLock = new();
        sessions.Roles = new();
        sessions.T2IPresets = _database.GetCollection<T2IPreset>("presets");
        User user = new(sessions, new User.DatabaseEntry { ID = "preset-test" });
        user.CalculatedRole.Data.PermissionFlags.Add("*");
        _session = new Session { User = user };

        T2IModelHandler models = new() { ModelType = "Stable-Diffusion" };
        T2IModelHandler loras = new() { ModelType = "LoRA" };
        Program.T2IModelSets = new() { ["Stable-Diffusion"] = models, ["LoRA"] = loras };
        _baseModel = new(models, "/tmp", "/tmp/Base.safetensors", "Base.safetensors");
        _editModel = new(models, "/tmp", "/tmp/Edit.safetensors", "Edit.safetensors");
        models.Models[_baseModel.Name] = _baseModel;
        models.Models[_editModel.Name] = _editModel;
        T2IModel lora = new(loras, "/tmp", "/tmp/Style.safetensors", "Style.safetensors");
        loras.Models[lora.Name] = lora;
    }

    private T2IPreset SavePreset(string name, Dictionary<string, string> values)
    {
        T2IPreset preset = new() { ID = $"preset-test///{name.ToLowerInvariant()}", Title = name, ParamMap = values };
        _session.User.SessionHandlerSource.T2IPresets.Upsert(preset);
        return preset;
    }

    private T2IParamInput Input(string prompt)
    {
        T2IParamInput input = new(_session);
        input.Set(T2IParamTypes.Model, _baseModel);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.NegativePrompt, "base negative");
        input.Set(T2IParamTypes.Steps, 10);
        input.Set(T2IParamTypes.CFGScale, 1);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseBase);
        input.Set(Base2EditExtension.ApplyEditAfter, "Base");
        input.Set(Base2EditExtension.EditSteps, 24);
        input.Set(Base2EditExtension.EditCFGScale, 1);
        input.Set(Base2EditExtension.EditSampler, "euler");
        input.Set(Base2EditExtension.EditScheduler, "normal");
        return input;
    }

    private static List<StageSpec> Parse(T2IParamInput input)
    {
        input.ApplySpecialLogic();
        input.ApplyLateSpecialLogic();
        return Base2EditSpecParser.Parse(new WorkflowGenerator { UserInput = input });
    }

    [Theory]
    [InlineData("preset")]
    [InlineData("p")]
    public void EditPreset_sets_sampler_and_confines_lora_without_changing_base(string tag)
    {
        SavePreset("QWEN21", new()
        {
            ["model"] = "Edit",
            ["steps"] = "16",
            ["cfgscale"] = "2",
            ["sampler"] = "dpmpp_2m",
            ["scheduler"] = "karras",
            ["loras"] = "Style",
            ["loraweights"] = "1.2",
            ["loratencweights"] = "0.7"
        });
        string prompt = $"base scene <edit><{tag}:QWEN21>dance hall";
        T2IParamInput input = Input(prompt);
        // Match a selected UI preset plus the same preset inside the edit prompt.
        input.PendingPresets.Add(new T2IPreset { ParamMap = new() { ["loras"] = "Style", ["loraweights"] = "0.5" } });
        input.ApplySpecialLogic();
        Assert.Same(_baseModel, input.Get(T2IParamTypes.Model));
        Assert.Equal(prompt, input.Get(T2IParamTypes.Prompt));

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input,
            WorkflowTestHarness.Template_BaseOnlyLatents().Concat(WorkflowTestHarness.Base2EditSteps()));
        JObject sampler = Assert.Single(workflow.Properties(), p => (string)p.Value["class_type"] == "KSamplerAdvanced").Value["inputs"] as JObject;
        Assert.Equal(16, (int)sampler["steps"]);
        Assert.Equal(2, (double)sampler["cfg"]);
        Assert.Equal("dpmpp_2m", (string)sampler["sampler_name"]);
        Assert.Equal("karras", (string)sampler["scheduler"]);
        Assert.Contains(workflow.Properties(), p => (string)p.Value["class_type"] == "CheckpointLoaderSimple"
            && (string)p.Value["inputs"]?["ckpt_name"] == _editModel.Name);
        JObject editLora = Assert.Single(workflow.Properties(), p => (string)p.Value["class_type"] == "LoraLoader"
            && (float)p.Value["inputs"]["strength_model"] == 1.2f).Value["inputs"] as JObject;
        Assert.Equal(0.7f, (float)editLora["strength_clip"]);
        Assert.Equal(10, input.Get(T2IParamTypes.Steps));
        Assert.Equal(1, input.Get(T2IParamTypes.CFGScale));
        Assert.Same(_baseModel, input.Get(T2IParamTypes.Model));
        Assert.Equal(new[] { "-1", $"{Base2EditExtension.SectionID_Edit}" }, input.Get(T2IParamTypes.LoraSectionConfinement));
        Assert.Equal(new[] { "0.5", "1.2" }, input.Get(T2IParamTypes.LoraWeights));
        Assert.Equal(prompt, input.ExtraMeta["original_prompt"]);
    }

    [Fact]
    public void IndexedPreset_overrides_card_and_inherits_to_child_without_affecting_sibling()
    {
        SavePreset("EditSettings", new() { ["steps"] = "16", ["cfgscale"] = "2" });
        T2IParamInput input = Input("base <edit[1]><preset:EditSettings>first <edit[2]>child <edit[3]>sibling");
        input.Set(Base2EditExtension.EditStages,
            ("[{\"ApplyAfter\":\"Base\",\"Model\":\"Use Base Model\",\"Steps\":30},"
            + "{\"ApplyAfter\":\"Edit Stage 1\",\"Model\":\"Use Base Model\"},"
            + "{\"ApplyAfter\":\"Base\",\"Model\":\"Use Base Model\"}]").Replace("Use Base Model", ModelPrep.UseBase));
        List<StageSpec> stages = Parse(input);
        Assert.Equal(24, stages[0].Steps);
        Assert.Equal(16, stages[1].Steps);
        Assert.Equal(16, stages[2].Steps);
        Assert.Equal(20, stages[3].Steps);
    }

    [Fact]
    public void Preset_prompt_templates_and_embedded_loras_stay_in_edit_section()
    {
        SavePreset("Style", new()
        {
            ["prompt"] = "beautiful {value} detailed <lora:Style:0.8>",
            ["negativeprompt"] = "blurry {value}"
        });
        T2IParamInput input = Input("base scene <edit><preset:Style>dance hall");
        input.Set(T2IParamTypes.NegativePrompt, "base negative <edit>bad lighting");
        StageSpec stage = Assert.Single(Parse(input));
        Assert.Equal("beautiful dance hall detailed", stage.PositivePrompt);
        Assert.Equal("blurry bad lighting", stage.NegativePrompt);
        Assert.Equal("base scene", new PromptRegion(input.Get(T2IParamTypes.Prompt)).GlobalPrompt.Trim());
        Assert.Equal("base negative", new PromptRegion(input.Get(T2IParamTypes.NegativePrompt)).GlobalPrompt.Trim());
        Assert.Single(stage.Loras.Names);
        Assert.Equal("0.8", stage.Loras.Weights[0]);
    }

    [Fact]
    public void GlobalPreset_keeps_core_behavior_and_later_base_section_is_not_deferred()
    {
        SavePreset("Base", new() { ["model"] = "Edit", ["steps"] = "12" });
        SavePreset("Edit", new() { ["steps"] = "16" });
        T2IParamInput input = Input("<preset:Base>base <edit><preset:Edit>edit <base><preset:Base>base only");
        input.ApplySpecialLogic();
        Assert.Same(_editModel, input.Get(T2IParamTypes.Model));
        input.ApplyLateSpecialLogic();
        Assert.Equal(12, input.Get(T2IParamTypes.Steps));
        Assert.Equal(16, Assert.Single(Base2EditSpecParser.Parse(new WorkflowGenerator { UserInput = input })).Steps);
    }

    [Fact]
    public void Missing_edit_preset_warns_without_changing_settings()
    {
        T2IParamInput input = Input("base <edit><preset:Missing>edit");
        StageSpec stage = Assert.Single(Parse(input));
        Assert.Equal(24, stage.Steps);
        Assert.Contains("Preset 'Missing' does not exist and will be ignored.", (List<string>)input.ExtraMeta["parser_warnings"]);
    }

    [Fact]
    public void Pending_preset_prompt_is_protected_and_generation_settings_do_not_leak()
    {
        SavePreset("Edit", new() { ["model"] = "Edit", ["images"] = "4", ["width"] = "1024", ["steps"] = "16" });
        T2IParamInput input = Input("base");
        input.Set(T2IParamTypes.Images, 2);
        input.PendingPresets.Add(new T2IPreset { ParamMap = new() { ["prompt"] = "{value} <edit><preset:Edit>edit" } });
        StageSpec stage = Assert.Single(Parse(input));
        Assert.Same(_baseModel, input.Get(T2IParamTypes.Model));
        Assert.Same(_editModel, stage.Model);
        Assert.Equal(2, input.Get(T2IParamTypes.Images));
        Assert.Equal(512, input.Get(T2IParamTypes.Width));
        Assert.Equal(16, stage.Steps);
    }

    [Theory]
    [InlineData("<preset:Settings><param[editsteps]:32>", 32)]
    [InlineData("<param[editsteps]:32><preset:Settings>", 16)]
    public void Preset_and_explicit_param_follow_prompt_order(string tags, int expected)
    {
        SavePreset("Settings", new() { ["steps"] = "16" });
        T2IParamInput input = Input($"base <edit>{tags}edit");
        Assert.Equal(expected, Assert.Single(Parse(input)).Steps);
    }

    public void Dispose()
    {
        _database.Dispose();
        _stream.Dispose();
        _context.Dispose();
    }
}
