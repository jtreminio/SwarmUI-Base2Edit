using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

internal static class Base2EditSpecParser
{
    private const string ApplyAfterBase = "Base";
    private const string ApplyAfterRefiner = "Refiner";
    private const double DefaultUpscale = 1.0;
    private const string DefaultUpscaleMethod = "pixel-lanczos";
    private const int DefaultSteps = 20;
    private const double DefaultControl = 1.0;
    private const double DefaultCfgScale = 7.0;

    public static List<StageSpec> Parse(WorkflowGenerator g)
    {
        bool hasRefinerPhaseWork = HasRefinerStageConfigured(g) || HasSegmentApplyAfterRefiner(g);

        Dictionary<int, StageSpec> stagesById = [];
        List<int> orderedIds = [];

        List<JObject> allEntries = [BuildStage0Shim(g), .. GetJsonStagesArray(g)];
        string posPrompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        StageSpec previousStage = null;
        for (int stageId = 0; stageId < allEntries.Count; stageId++)
        {
            JObject obj = allEntries[stageId];
            if (GetOptionalBool(obj, "Skipped", defaultValue: false))
            {
                continue;
            }

            StageSpec parsed = ParseStage(
                g,
                obj,
                stageId,
                previousStage,
                hasRefinerPhaseWork,
                stagesById,
                posPrompt,
                negPrompt,
                posOriginal: PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.Prompt.Type.ID, posPrompt),
                negOriginal: PromptParser.GetOriginalPrompt(g.UserInput, T2IParamTypes.NegativePrompt.Type.ID, negPrompt),
                loraInputs: new LoraInputsSnapshot(g),
                baseSeed: g.UserInput.Get(T2IParamTypes.Seed),
                guidance: g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1));

            stagesById[stageId] = parsed;
            orderedIds.Add(stageId);
            previousStage = parsed;
        }

        return AttachEditStageChildren(stagesById, orderedIds);
    }

    private static List<StageSpec> AttachEditStageChildren(
        Dictionary<int, StageSpec> stagesById,
        List<int> orderedIds)
    {
        Dictionary<int, List<int>> childIdsByParent = [];
        foreach (int id in orderedIds)
        {
            StageSpec stage = stagesById[id];
            if (stage.ParentKind == ParentKind.Edit)
            {
                if (!childIdsByParent.TryGetValue(stage.ParentStageId, out List<int> list))
                {
                    list = [];
                    childIdsByParent[stage.ParentStageId] = list;
                }
                list.Add(id);
            }
        }

        Dictionary<int, StageSpec> finalized = [];
        foreach (int id in orderedIds.AsEnumerable().Reverse())
        {
            StageSpec stage = stagesById[id];
            if (childIdsByParent.TryGetValue(id, out List<int> childIds))
            {
                stage = stage with { Children = [.. childIds.Select(cid => finalized[cid])] };
            }
            finalized[id] = stage;
        }

        return [.. orderedIds.Select(id => finalized[id])];
    }

    private static JObject BuildStage0Shim(WorkflowGenerator g)
    {
        string applyAfterRaw = g.UserInput.Get(Base2EditExtension.ApplyEditAfter);
        string applyAfter = StringUtils.Equals(applyAfterRaw, ApplyAfterBase)
                || StringUtils.Equals(applyAfterRaw, ApplyAfterRefiner)
            ? applyAfterRaw
            : ApplyAfterRefiner;

        JObject shim = new()
        {
            ["ApplyAfter"] = applyAfter,
            ["KeepPreEditImage"] = g.UserInput.Get(Base2EditExtension.KeepPreEditImage),
            ["RefineOnly"] = g.UserInput.Get(Base2EditExtension.EditRefineOnly),
            ["Control"] = g.UserInput.Get(Base2EditExtension.EditControl),
        };
        if (g.UserInput.TryGet(Base2EditExtension.EditModel, out string editModel)
            && !string.IsNullOrWhiteSpace(editModel))
        {
            shim["Model"] = editModel;
        }
        if (g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel vaeModel)
            && vaeModel is not null
            && !string.IsNullOrWhiteSpace(vaeModel.Name))
        {
            shim["Vae"] = vaeModel.Name;
        }
        shim["Upscale"] = g.UserInput.Get(Base2EditExtension.EditUpscale, DefaultUpscale);
        if (g.UserInput.TryGet(Base2EditExtension.EditUpscaleMethod, out string upscaleMethod)
            && !string.IsNullOrWhiteSpace(upscaleMethod))
        {
            shim["UpscaleMethod"] = upscaleMethod;
        }
        if (g.UserInput.TryGet(Base2EditExtension.EditSteps, out int steps))
        {
            shim["Steps"] = steps;
        }
        if (g.UserInput.TryGet(Base2EditExtension.EditCFGScale, out double cfg))
        {
            shim["CfgScale"] = cfg;
        }
        if (g.UserInput.TryGet(Base2EditExtension.EditSampler, out string sampler)
            && !string.IsNullOrWhiteSpace(sampler))
        {
            shim["Sampler"] = sampler;
        }
        if (g.UserInput.TryGet(Base2EditExtension.EditScheduler, out string scheduler)
            && !string.IsNullOrWhiteSpace(scheduler))
        {
            shim["Scheduler"] = scheduler;
        }
        return shim;
    }

    private static StageSpec ParseStage(
        WorkflowGenerator g,
        JObject obj,
        int stageId,
        StageSpec previousStage,
        bool hasRefinerPhaseWork,
        Dictionary<int, StageSpec> stagesById,
        string posPrompt,
        string negPrompt,
        string posOriginal,
        string negOriginal,
        LoraInputsSnapshot loraInputs,
        long baseSeed,
        double guidance)
    {
        string locationPrefix = $"Edit Stage {stageId}";

        string defaultApplyAfterStr = previousStage is null
            ? ApplyAfterRefiner
            : StageRefStore.FormatStageLabel(previousStage.Id);
        string applyAfterRaw = GetOptionalString(obj, "ApplyAfter", defaultApplyAfterStr, locationPrefix, allowEmpty: false);
        (ParentKind parentKind, int parentStageId) = NormalizeApplyAfter(applyAfterRaw, stageId, locationPrefix);

        if (!hasRefinerPhaseWork && parentKind == ParentKind.Refiner)
        {
            parentKind = ParentKind.Base;
        }

        if (parentKind == ParentKind.Edit)
        {
            if (parentStageId >= stageId)
            {
                throw new SwarmUserErrorException(
                    $"Base2Edit: {locationPrefix} cannot Apply After "
                    + $"'{StageRefStore.FormatStageLabel(parentStageId)}' (must reference an earlier stage).");
            }
            if (!stagesById.ContainsKey(parentStageId))
            {
                throw new SwarmUserErrorException(
                    $"Base2Edit: {locationPrefix} cannot Apply After "
                    + $"'{StageRefStore.FormatStageLabel(parentStageId)}' because that stage is missing or invalid.");
            }
        }

        (T2IModel resolvedModel, ModelSource modelSource) = ResolveStageModel(g, obj, locationPrefix);
        T2IModel resolvedVae = ResolveStageVae(obj, locationPrefix);

        return new StageSpec(
            Id: stageId,
            ParentKind: parentKind,
            ParentStageId: parentStageId,
            KeepPreEditImage: GetOptionalBool(obj, "KeepPreEditImage", previousStage?.KeepPreEditImage ?? false),
            RefineOnly: GetOptionalBool(obj, "RefineOnly", previousStage?.RefineOnly ?? false),
            Control: NormalizeControl(GetOptionalDouble(obj, "Control", DefaultControl, locationPrefix)),
            Model: resolvedModel,
            ModelSource: modelSource,
            Vae: resolvedVae,
            Upscale: NormalizeUpscale(GetOptionalDouble(obj, "Upscale", DefaultUpscale, locationPrefix)),
            UpscaleMethod: GetOptionalString(obj, "UpscaleMethod", DefaultUpscaleMethod, locationPrefix, allowEmpty: false),
            Steps: GetOptionalInt(obj, "Steps", DefaultSteps, locationPrefix),
            CfgScale: NormalizeCfgScale(GetOptionalDouble(obj, "CfgScale", g.UserInput.Get(T2IParamTypes.CFGScale, DefaultCfgScale), locationPrefix)),
            Sampler: ParseOptionalString(obj, "Sampler", locationPrefix),
            Scheduler: ParseOptionalString(obj, "Scheduler", locationPrefix),
            PositivePrompt: PromptParser.ExtractPrompt(posPrompt, posOriginal, stageId),
            NegativePrompt: PromptParser.ExtractPrompt(negPrompt, negOriginal, stageId),
            Loras: (PromptParser.HasAnyEditSectionForStage(posPrompt, stageId)
                    || PromptParser.HasAnyEditSectionForStage(negPrompt, stageId))
                ? loraInputs.FilterForStage(stageId)
                : StageLoras.Empty,
            Seed: baseSeed + Base2EditExtension.EditSeedOffset + stageId,
            Guidance: guidance,
            Children: []
        );
    }

    private static (T2IModel Model, ModelSource Source) ResolveStageModel(
        WorkflowGenerator g,
        JObject obj,
        string locationPrefix)
    {
        string raw = GetString(obj, "Model");
        if (raw is null || string.IsNullOrWhiteSpace(raw))
        {
            throw new SwarmUserErrorException(
                $"Base2Edit: {locationPrefix} is missing required field 'Model'.");
        }
        return ModelPrep.ResolveSelection(g, raw.Trim(), locationPrefix);
    }

    private static T2IModel ResolveStageVae(JObject obj, string locationPrefix)
    {
        if (!JsonHasOwnProperty(obj, "Vae"))
        {
            return null;
        }
        string raw = GetString(obj, "Vae");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        string trimmed = raw.Trim();
        if (StringUtils.Equals(trimmed, "None") || StringUtils.Equals(trimmed, "Automatic"))
        {
            return null;
        }
        if (!Program.T2IModelSets.TryGetValue("VAE", out T2IModelHandler handler))
        {
            throw new SwarmUserErrorException(
                $"Base2Edit: {locationPrefix} references VAE '{trimmed}' but no VAE handler is available.");
        }
        if (handler.Models.TryGetValue(trimmed, out T2IModel direct))
        {
            return direct;
        }
        foreach ((string _, T2IModel candidate) in handler.Models)
        {
            if (StringUtils.Equals(candidate.Name, trimmed)
                || StringUtils.Equals(T2IParamTypes.CleanModelName(candidate.Name), trimmed))
            {
                return candidate;
            }
        }
        throw new SwarmUserErrorException(
            $"Base2Edit: {locationPrefix} references unknown VAE '{trimmed}'.");
    }

    private static List<JObject> GetJsonStagesArray(WorkflowGenerator g)
    {
        if (!g.UserInput.TryGet(Base2EditExtension.EditStages, out string json)
            || string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JToken token;
        try
        {
            token = JToken.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SwarmUserErrorException(
                $"Base2Edit: Could not parse Edit Stages JSON. {ex.Message}");
        }

        if (token is not JArray arr)
        {
            throw new SwarmUserErrorException(
                "Base2Edit: Edit Stages JSON must be an array.");
        }

        List<JObject> entries = [];
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JObject entry)
            {
                throw new SwarmUserErrorException(
                    $"Base2Edit: Edit Stage entry at index {i} is not an object.");
            }
            entries.Add(entry);
        }
        return entries;
    }

    private static (ParentKind Kind, int StageId) NormalizeApplyAfter(string applyAfterRaw, int stageId, string locationPrefix)
    {
        string applyAfter = applyAfterRaw.Trim();
        if (StringUtils.Equals(applyAfter, ApplyAfterBase))
        {
            return (ParentKind.Base, -1);
        }
        if (StringUtils.Equals(applyAfter, ApplyAfterRefiner))
        {
            return (ParentKind.Refiner, -1);
        }
        if (StageRefStore.TryParseStageIndexKey(applyAfter, out int parentId))
        {
            return (ParentKind.Edit, parentId);
        }
        throw new SwarmUserErrorException(
            $"Base2Edit: {locationPrefix} has invalid Apply After '{applyAfterRaw}'.");
    }

    private static bool HasRefinerStageConfigured(WorkflowGenerator g)
    {
        if (T2IParamTypes.RefinerMethod?.Type is null || T2IParamTypes.RefinerControl?.Type is null)
        {
            return false;
        }

        return g.UserInput.TryGetRaw(T2IParamTypes.RefinerMethod.Type, out object _)
            && g.UserInput.TryGetRaw(T2IParamTypes.RefinerControl.Type, out object _);
    }

    private static bool HasSegmentApplyAfterRefiner(WorkflowGenerator g)
    {
        string segmentApplyAfter = g.UserInput.Get(T2IParamTypes.SegmentApplyAfter, ApplyAfterRefiner);
        if (!StringUtils.Equals(segmentApplyAfter, ApplyAfterRefiner))
        {
            return false;
        }

        PromptRegion prompt = new(g.UserInput.Get(T2IParamTypes.Prompt, ""));
        return prompt.Parts.Any(p => p.Type == PromptRegion.PartType.Segment);
    }

    private static double TruncateToDecimals(double value, int decimals)
    {
        double factor = Math.Pow(10, decimals);
        return Math.Truncate(value * factor) / factor;
    }

    private static double NormalizeControl(double control) => TruncateToDecimals(Math.Clamp(control, 0, 1), 2);

    private static double NormalizeUpscale(double upscale) => TruncateToDecimals(upscale, 2);

    private static double NormalizeCfgScale(double cfgScale) => TruncateToDecimals(cfgScale, 1);

    private static string ParseOptionalString(JObject obj, string key, string locationPrefix)
    {
        string value = GetString(obj, key);
        if (value is null)
        {
            return null;
        }
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            Logs.Warning($"Base2Edit: {locationPrefix} has empty field '{key}'. Leaving unset.");
            return null;
        }
        return value;
    }

    private static string GetString(JObject obj, string key)
    {
        foreach (JProperty p in obj.Properties())
        {
            if (StringUtils.Equals(p.Name, key))
            {
                return p.Value?.Type == JTokenType.Null ? null : $"{p.Value}";
            }
        }
        return null;
    }

    private static string GetOptionalString(
        JObject obj,
        string key,
        string defaultValue,
        string locationPrefix,
        bool allowEmpty)
    {
        string value = GetString(obj, key);
        if (value is null)
        {
            return defaultValue;
        }
        value = value.Trim();
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            Logs.Warning($"Base2Edit: {locationPrefix} has empty field '{key}'. Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static int GetOptionalInt(JObject obj, string key, int defaultValue, string locationPrefix)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!int.TryParse(raw.Trim(), out int value))
        {
            Logs.Warning(
                $"Base2Edit: {locationPrefix} has invalid integer field '{key}' value '{raw}'. "
                + $"Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static double GetOptionalDouble(JObject obj, string key, double defaultValue, string locationPrefix)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!double.TryParse(raw.Trim(), out double value))
        {
            Logs.Warning(
                $"Base2Edit: {locationPrefix} has invalid numeric field '{key}' value '{raw}'. "
                + $"Using default '{defaultValue}'.");
            return defaultValue;
        }
        return value;
    }

    private static bool GetOptionalBool(JObject obj, string key, bool defaultValue)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        return bool.TryParse(raw.Trim(), out bool value) ? value : defaultValue;
    }

    private static bool JsonHasOwnProperty(JObject obj, string key) =>
        obj.Properties().Any(p => StringUtils.Equals(p.Name, key));

    private sealed class LoraInputsSnapshot
    {
        private readonly List<string> _loras;
        private readonly List<string> _weights;
        private readonly List<string> _tencWeights;
        private readonly List<string> _confinements;

        public LoraInputsSnapshot(WorkflowGenerator g)
        {
            g.UserInput.TryGet(T2IParamTypes.Loras, out _loras);
            _loras ??= [];
            _weights = g.UserInput.Get(T2IParamTypes.LoraWeights) ?? [];
            _tencWeights = g.UserInput.Get(T2IParamTypes.LoraTencWeights) ?? [];
            _confinements = g.UserInput.Get(T2IParamTypes.LoraSectionConfinement) ?? [];
        }

        public StageLoras FilterForStage(int stageIndex)
        {
            if (_loras.Count == 0 || _confinements.Count == 0)
            {
                return StageLoras.Empty;
            }

            int globalCid = Base2EditExtension.SectionID_Edit;
            int stageCid = Base2EditExtension.EditSectionIdForStage(stageIndex);
            List<string> outNames = [];
            List<string> outWeights = [];
            List<string> outTencWeights = [];

            for (int i = 0; i < _loras.Count; i++)
            {
                if (i >= _confinements.Count)
                {
                    continue;
                }
                if (!int.TryParse(_confinements[i], out int confinementId))
                {
                    continue;
                }
                if (confinementId != globalCid && confinementId != stageCid)
                {
                    continue;
                }
                outNames.Add(_loras[i]);
                outWeights.Add(i < _weights.Count ? _weights[i] : "1");
                outTencWeights.Add(i < _tencWeights.Count ? _tencWeights[i] : (i < _weights.Count ? _weights[i] : "1"));
            }

            return outNames.Count == 0
                ? StageLoras.Empty
                : new StageLoras(outNames, outWeights, outTencWeights);
        }
    }
}
