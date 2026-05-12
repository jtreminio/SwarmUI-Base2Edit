using ComfyTyped.Core;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public class StageRefStore(WorkflowGenerator g)
{
    private const string Prefix = "b2e.";
    private const string PublishedEditPrefix = "b2e.published.edit.";
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
        if (data?.Path is not JArray { Count: 2 } arr)
        {
            g.NodeHelpers.Remove(key);
            return;
        }
        g.NodeHelpers[key] = string.Join("|",
            $"{arr[0]}", $"{arr[1]}",
            data.DataType ?? WGNodeData.DT_IMAGE,
            data.Width.HasValue ? $"{data.Width.Value}" : "",
            data.Height.HasValue ? $"{data.Height.Value}" : "",
            data.Compat?.ID ?? "");
    }

    private WGNodeData LoadNodeData(string key, string fallbackDataType, WGNodeData fallbackVae = null)
    {
        if (!g.NodeHelpers.TryGetValue(key, out string encoded) || string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }
        string[] parts = encoded.Split('|');
        if (parts.Length < 5 || !int.TryParse(parts[1], out int slot))
        {
            return null;
        }
        string nodeId = parts[0];
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ComfyNode node = bridge.Graph.GetNode(nodeId);
        if (node is null)
        {
            Logs.Warning($"Base2Edit: node '{nodeId}' not found in workflow; treating as not captured.");
            return null;
        }
        INodeOutput output = node.FindOutput(slot)
            ?? (node is UnknownNode u ? u.GetOutput(slot) : null);
        if (output is null)
        {
            Logs.Warning($"Base2Edit: slot {slot} on node '{nodeId}' not found; treating as not captured.");
            return null;
        }
        string dataType = !string.IsNullOrEmpty(parts[2]) ? parts[2] : fallbackDataType;
        string compatId = parts.Length >= 6 ? parts[5] : "";
        T2IModelCompatClass compat = ResolveCompatFor(dataType, fallbackVae, compatId);
        return new WGNodeData(WorkflowBridge.ToPath(output), g, dataType, compat)
        {
            Width = Nullable(parts[3]),
            Height = Nullable(parts[4])
        };
    }

    private bool HasCaptured(StageKind kind, int? index = null) =>
        g.NodeHelpers.ContainsKey(NodeKey(kind, index, "model"));

    private StageRef LoadStageRef(StageKind kind, int? index = null)
    {
        WGNodeData vae = LoadNodeData(NodeKey(kind, index, "vae"), WGNodeData.DT_VAE);
        return new(
            Model: LoadNodeData(NodeKey(kind, index, "model"), WGNodeData.DT_MODEL, vae),
            TextEnc: LoadNodeData(NodeKey(kind, index, "clip"), WGNodeData.DT_TEXTENC, vae),
            Media: LoadNodeData(NodeKey(kind, index, "media"), WGNodeData.DT_LATENT_IMAGE, vae),
            Vae: vae
        );
    }

    private T2IModelCompatClass ResolveCompatFor(string dataType, WGNodeData fallbackVae, string compatId)
    {
        if (!string.IsNullOrWhiteSpace(compatId)
            && T2IModelClassSorter.CompatClasses.TryGetValue(compatId.ToLowerFast(), out T2IModelCompatClass c))
        {
            return c;
        }
        if (dataType == WGNodeData.DT_AUDIO || dataType == WGNodeData.DT_LATENT_AUDIO || dataType == WGNodeData.DT_AUDIOVAE)
        {
            return g.CurrentAudioVae?.Compat;
        }
        if (dataType == WGNodeData.DT_VAE && g.CurrentVae is not null)
        {
            return g.CurrentVae.Compat;
        }
        return fallbackVae?.Compat ?? g.CurrentVae?.Compat ?? g.CurrentCompat();
    }

    public StageRef Base => HasCaptured(StageKind.Base) ? LoadStageRef(StageKind.Base) : null;
    public StageRef Refiner => HasCaptured(StageKind.Refiner) ? LoadStageRef(StageKind.Refiner) : null;

    public void Capture(StageKind stage, int? index = null)
    {
        StoreNodeData(NodeKey(stage, index, "model"), g.CurrentModel);
        StoreNodeData(NodeKey(stage, index, "clip"), g.CurrentTextEnc);
        StoreNodeData(NodeKey(stage, index, "media"), g.CurrentMedia);
        StoreNodeData(NodeKey(stage, index, "vae"), g.CurrentVae);
        PublishEditStageRef(stage, index, g.CurrentMedia, g.CurrentVae);
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

    public bool DiscardEdit(int index)
    {
        bool removedModel = g.NodeHelpers.Remove(NodeKey(StageKind.Edit, index, "model"));
        bool removedClip = g.NodeHelpers.Remove(NodeKey(StageKind.Edit, index, "clip"));
        bool removedMedia = g.NodeHelpers.Remove(NodeKey(StageKind.Edit, index, "media"));
        bool removedVae = g.NodeHelpers.Remove(NodeKey(StageKind.Edit, index, "vae"));
        g.NodeHelpers.Remove(PublishedEditNodeKey(index));
        return removedModel || removedClip || removedMedia || removedVae;
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

    private static string PublishedEditNodeKey(int index) => $"{PublishedEditPrefix}{index}";

    private void PublishEditStageRef(StageKind stage, int? index, WGNodeData media, WGNodeData vae)
    {
        if (stage != StageKind.Edit || index is null)
        {
            return;
        }

        string key = PublishedEditNodeKey(index.Value);
        JObject mediaObj = SerializeNodeData(media);
        if (mediaObj is null)
        {
            g.NodeHelpers.Remove(key);
            return;
        }

        JObject payload = new()
        {
            ["media"] = mediaObj
        };
        JObject vaeObj = SerializeNodeData(vae);
        if (vaeObj is not null)
        {
            payload["vae"] = vaeObj;
        }
        g.NodeHelpers[key] = payload.ToString(Formatting.None);
    }

    private static JObject SerializeNodeData(WGNodeData data)
    {
        if (data?.Path is not JArray path || path.Count != 2)
        {
            return null;
        }

        JObject result = new()
        {
            ["path"] = new JArray(path[0], path[1]),
            ["dataType"] = data.DataType
        };
        if (!string.IsNullOrWhiteSpace(data.Compat?.ID))
        {
            result["compatId"] = data.Compat.ID;
        }
        if (data.Width.HasValue)
        {
            result["width"] = data.Width.Value;
        }
        if (data.Height.HasValue)
        {
            result["height"] = data.Height.Value;
        }
        if (data.Frames.HasValue)
        {
            result["frames"] = data.Frames.Value;
        }
        if (data.FPS.HasValue)
        {
            result["fps"] = data.FPS.Value;
        }
        return result;
    }

    public static string FormatStageLabel(int stageIndex) => $"{EditStagePrefix}{stageIndex}";

    private static int? Nullable(string s) =>
        !string.IsNullOrEmpty(s) && int.TryParse(s, out int v) ? v : null;
}
