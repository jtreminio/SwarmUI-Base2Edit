using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class StageOutputMetadataTests
{
    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BaseSteps() =>
        WorkflowTestHarness.Template_BaseOnlyLatents()
            .Concat(WorkflowTestHarness.Base2EditSteps());

    private static T2IParamInput BuildPreEditInput()
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Refiner");
        input.Set(Base2EditExtension.KeepPreEditImage, true);
        input.Set(T2IParamTypes.Seed, 1);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        return input;
    }

    [Fact]
    public void Pre_edit_save_records_stage_node_map()
    {
        T2IParamInput input = BuildPreEditInput();

        JObject workflow = WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SaveImageNode save = Assert.Single(bridge.Graph.NodesOfType<SaveImageNode>());

        Assert.True(input.ExtraMeta.TryGetValue(Base2EditExtension.StageNodeMapKey, out object raw));
        Dictionary<string, string> map = Assert.IsType<Dictionary<string, string>>(raw);
        Assert.True(map.TryGetValue(save.Id, out string label));
        Assert.Contains("pre-edit input", label);
    }

    [Fact]
    public void PostGenerate_tags_image_from_node_id_and_removes_raw_map()
    {
        T2IParamInput input = BuildPreEditInput();
        WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Dictionary<string, string> map = (Dictionary<string, string>)input.ExtraMeta[Base2EditExtension.StageNodeMapKey];
        KeyValuePair<string, string> entry = map.First();

        T2IParamInput perImage = input.Clone();
        T2IEngine.PostGenerateEvent?.Invoke(new T2IEngine.PostGenerationEventParams(null, perImage, () => { }, entry.Key));

        Assert.Equal(entry.Value, perImage.ExtraMeta[Base2EditExtension.StageMetadataKey]);
        Assert.False(perImage.ExtraMeta.ContainsKey(Base2EditExtension.StageNodeMapKey));
    }

    [Fact]
    public void Sibling_images_resolve_labels_independently()
    {
        T2IParamInput input = BuildPreEditInput();
        WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        Dictionary<string, string> map = (Dictionary<string, string>)input.ExtraMeta[Base2EditExtension.StageNodeMapKey];
        KeyValuePair<string, string> entry = map.First();

        T2IParamInput imageA = input.Clone();
        T2IParamInput imageB = input.Clone();
        T2IEngine.PostGenerateEvent?.Invoke(new T2IEngine.PostGenerationEventParams(null, imageA, () => { }, entry.Key));
        T2IEngine.PostGenerateEvent?.Invoke(new T2IEngine.PostGenerationEventParams(null, imageB, () => { }, entry.Key));

        Assert.Equal(entry.Value, imageA.ExtraMeta[Base2EditExtension.StageMetadataKey]);
        Assert.Equal(entry.Value, imageB.ExtraMeta[Base2EditExtension.StageMetadataKey]);
    }

    [Fact]
    public void Primary_and_sibling_branch_both_get_distinct_stage_labels()
    {
        WorkflowTestHarness.Base2EditSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "global <edit>do the edit");
        input.Set(Base2EditExtension.EditModel, ModelPrep.UseRefiner);
        input.Set(Base2EditExtension.ApplyEditAfter, "Refiner");
        input.Set(T2IParamTypes.Seed, 1);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);

        JArray stages = new(new JObject
        {
            ["applyAfter"] = "Refiner",
            ["keepPreEditImage"] = false,
            ["refineOnly"] = true,
            ["control"] = 0.3,
            ["model"] = ModelPrep.UseBase,
            ["steps"] = 10,
            ["cfgScale"] = 1.0
        });
        input.Set(Base2EditExtension.EditStages, stages.ToString());

        WorkflowTestHarness.GenerateWithSteps(input, WorkflowTestHarness.Template_BaseThenRefiner().Concat(WorkflowTestHarness.Base2EditSteps()));

        Dictionary<string, string> map = (Dictionary<string, string>)input.ExtraMeta[Base2EditExtension.StageNodeMapKey];
        Assert.Equal("Edit Stage 0 (final output)", map["9"]);
        Assert.Contains(map.Values, v => v == "Edit Stage 1 (branch output)");
    }

    [Fact]
    public void PostGenerate_without_matching_node_removes_raw_map_without_tagging()
    {
        T2IParamInput input = BuildPreEditInput();
        WorkflowTestHarness.GenerateWithSteps(input, BaseSteps());

        T2IParamInput perImage = input.Clone();
        T2IEngine.PostGenerateEvent?.Invoke(new T2IEngine.PostGenerationEventParams(null, perImage, () => { }, "424242"));

        Assert.False(perImage.ExtraMeta.ContainsKey(Base2EditExtension.StageMetadataKey));
        Assert.False(perImage.ExtraMeta.ContainsKey(Base2EditExtension.StageNodeMapKey));
    }
}
