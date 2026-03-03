using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace Base2Edit;

public class StageRefStore(WorkflowGenerator g)
{
    private const string Key = "base2edit.ref_store";
    private const string EditStagePrefix = "Edit Stage ";
    private const string EditAliasPrefix = "edit";

    public enum StageKind
    {
        Base,
        Refiner,
        Edit,
        Prompt
    }

    public sealed record StageRef(
        WGNodeData Model,
        WGNodeData TextEnc,
        WGNodeData Media,
        WGNodeData Vae
    );

    private sealed class StoreData
    {
        public StageRef Base;
        public StageRef Refiner;
        public Dictionary<int, StageRef> Edit { get; } = [];
        public Dictionary<int, StageRef> Prompt { get; } = [];
        public Dictionary<int, JsonParser.StageSpec> ParsedStages { get; } = [];
    }

    private StoreData GetStore()
    {
        if (g.UserInput.ExtraMeta is not null
            && g.UserInput.ExtraMeta.TryGetValue(Key, out object existingObj)
            && existingObj is StoreData existing)
        {
            return existing;
        }

        StoreData store = new();
        g.UserInput.ExtraMeta[Key] = store;
        return store;
    }

    public void ResetStore()
    {
        g.UserInput.ExtraMeta[Key] = new StoreData();
    }

    public void DeleteStore()
    {
        g.UserInput.ExtraMeta.Remove(Key);
    }

    public StageRef Base => GetStore().Base;
    public StageRef Refiner => GetStore().Refiner;
    public Dictionary<int, StageRef> Edit => GetStore().Edit;
    public Dictionary<int, StageRef> Prompt => GetStore().Prompt;
    public Dictionary<int, JsonParser.StageSpec> ParsedStages => GetStore().ParsedStages;

    public void Capture(StageKind stage, int? index = null)
    {
        StageRef stageRef = new(
            Model: g.CurrentModel,
            TextEnc: g.CurrentTextEnc,
            Media: g.CurrentMedia,
            Vae: g.CurrentVae
        );

        switch (stage)
        {
            case StageKind.Base:
                GetStore().Base = stageRef;
                break;
            case StageKind.Refiner:
                GetStore().Refiner = stageRef;
                break;
            case StageKind.Edit:
                GetStore().Edit[index ?? 0] = stageRef;
                break;
            case StageKind.Prompt:
                GetStore().Prompt[index ?? 0] = stageRef;
                break;
        }
    }

    public void SetParsedStages(IEnumerable<JsonParser.StageSpec> stages)
    {
        Dictionary<int, JsonParser.StageSpec> parsed = GetStore().ParsedStages;
        parsed.Clear();
        foreach (JsonParser.StageSpec stage in stages ?? [])
        {
            parsed[stage.Id] = stage;
        }
    }

    public bool TryGetCapturedModelState(
        StageKind stageKind,
        out JArray model,
        out JArray clip,
        out JArray vae)
    {
        model = null;
        clip = null;
        vae = null;

        StageRef stageRef = stageKind switch
        {
            StageKind.Base => Base,
            StageKind.Refiner => Refiner,
            _ => null
        };

        if (stageRef?.Model?.Path is not JArray m || m.Count != 2
            || stageRef.TextEnc?.Path is not JArray c || c.Count != 2
            || stageRef.Vae?.Path is not JArray v || v.Count != 2)
        {
            return false;
        }

        model = m;
        clip = c;
        vae = v;
        return true;
    }

    public static bool TryParseStageIndexKey(string rawValue, out int stageIndex)
    {
        stageIndex = -1;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        string value = rawValue.Trim();
        if (value.StartsWith(EditStagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[EditStagePrefix.Length..].Trim();
        }
        else if (value.StartsWith(EditAliasPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[EditAliasPrefix.Length..].Trim();
        }
        else
        {
            return false;
        }

        return int.TryParse(value, out stageIndex) && stageIndex >= 0;
    }

    public static string FormatStageLabel(int stageIndex) => $"{EditStagePrefix}{stageIndex}";
}
