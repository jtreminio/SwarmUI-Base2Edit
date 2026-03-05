using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace Base2Edit;

public class StageRefStore(WorkflowGenerator g)
{
    private const string Prefix = "b2e.";
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

    private static string StageName(StageKind kind, int? index) => kind switch
    {
        StageKind.Base => "base",
        StageKind.Refiner => "refiner",
        StageKind.Edit => $"edit.{index ?? 0}",
        StageKind.Prompt => $"prompt.{index ?? 0}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string NodeKey(StageKind kind, int? index, string property) =>
        $"{Prefix}{StageName(kind, index)}.{property}";

    private void StoreNodeData(string key, WGNodeData data)
    {
        if (data?.Path is JArray arr && arr.Count == 2)
        {
            int width = data.Width ?? g.UserInput.GetImageWidth();
            int height = data.Height ?? g.UserInput.GetImageHeight();
            g.NodeHelpers[key] = $"{arr[0]}|{arr[1]}|{data.DataType}|{width}|{height}";
        }
    }

    private WGNodeData LoadNodeData(string key, string fallbackDataType)
    {
        if (!g.NodeHelpers.TryGetValue(key, out string encoded) || string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        string[] parts = encoded.Split('|');
        if (parts.Length < 5)
        {
            return null;
        }

        JArray path = new(parts[0], int.Parse(parts[1]));
        string dataType = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : fallbackDataType;
        if (!int.TryParse(parts[3], out int width))
        {
            return null;
        }
        if (!int.TryParse(parts[4], out int height))
        {
            return null;
        }

        WGNodeData nodeData = new(path, g, dataType, g.CurrentCompat())
        {
            Width = width,
            Height = height
        };

        return nodeData;
    }

    private bool HasCaptured(StageKind kind, int? index = null) =>
        g.NodeHelpers.ContainsKey(NodeKey(kind, index, "model"));

    private StageRef LoadStageRef(StageKind kind, int? index = null) => new(
        Model: LoadNodeData(NodeKey(kind, index, "model"), WGNodeData.DT_MODEL),
        TextEnc: LoadNodeData(NodeKey(kind, index, "clip"), WGNodeData.DT_TEXTENC),
        Media: LoadNodeData(NodeKey(kind, index, "media"), WGNodeData.DT_LATENT_IMAGE),
        Vae: LoadNodeData(NodeKey(kind, index, "vae"), WGNodeData.DT_VAE)
    );

    public StageRef Base => HasCaptured(StageKind.Base) ? LoadStageRef(StageKind.Base) : null;
    public StageRef Refiner => HasCaptured(StageKind.Refiner) ? LoadStageRef(StageKind.Refiner) : null;

    public void Capture(StageKind stage, int? index = null)
    {
        StoreNodeData(NodeKey(stage, index, "model"), g.CurrentModel);
        StoreNodeData(NodeKey(stage, index, "clip"), g.CurrentTextEnc);
        StoreNodeData(NodeKey(stage, index, "media"), g.CurrentMedia);
        StoreNodeData(NodeKey(stage, index, "vae"), g.CurrentVae);
    }

    public bool TryGetEditRef(int index, out StageRef stageRef)
    {
        stageRef = null;
        if (!HasCaptured(StageKind.Edit, index))
        {
            return false;
        }

        stageRef = LoadStageRef(StageKind.Edit, index);
        return true;
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

        if (!HasCaptured(stageKind))
        {
            return false;
        }

        StageRef stageRef = LoadStageRef(stageKind);

        if (stageRef.Model?.Path is not JArray m || m.Count != 2
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
