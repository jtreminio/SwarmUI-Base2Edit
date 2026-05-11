using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

internal static class Base2EditSpecParser
{
    private const string ApplyAfterBase = "Base";
    private const string ApplyAfterRefiner = "Refiner";
    private const string DefaultApplyAfter = ApplyAfterRefiner;
    private const double DefaultUpscale = 1.0;
    private const string DefaultUpscaleMethod = "pixel-lanczos";
    private const int DefaultSteps = 20;
    private const double DefaultCfgScale = 7.0;
    private const string DefaultSampler = "euler";
    private const string DefaultScheduler = "normal";
    private const string DefaultVae = "None";

    private sealed record StageDefaults(
        string Model,
        string Vae,
        double Upscale,
        string UpscaleMethod,
        int Steps,
        double CfgScale,
        string Sampler,
        string Scheduler
    );

    public static List<StageSpec> Parse(WorkflowGenerator g)
    {
        bool hasRefinerPhaseWork = HasRefinerStageConfigured(g) || HasSegmentApplyAfterRefiner(g);
        StageDefaults baseDefaults = ResolveBaseDefaults(g);
        StageDefaults refinerDefaults = ResolveRefinerDefaults(g, baseDefaults);

        Dictionary<int, StageSpec> stagesById = [];
        Dictionary<int, StageDefaults> defaultsById = [];
        List<int> orderedIds = [];

        StageSpec stage0 = ParseStage0(g, baseDefaults, refinerDefaults, hasRefinerPhaseWork);
        stagesById[0] = stage0;
        defaultsById[0] = ToStageDefaults(stage0);
        orderedIds.Add(0);

        StageSpec previousStage = stage0;
        int nextId = 1;
        foreach (JObject obj in GetJsonStagesArray(g))
        {
            int stageId = nextId++;
            if (GetOptionalBool(obj, "Skipped", defaultValue: false))
            {
                continue;
            }

            StageSpec parsed = ParseJsonStage(
                obj,
                stageId,
                previousStage,
                baseDefaults,
                refinerDefaults,
                hasRefinerPhaseWork,
                stagesById,
                defaultsById);
            if (parsed is null)
            {
                continue;
            }

            stagesById[stageId] = parsed;
            defaultsById[stageId] = ToStageDefaults(parsed);
            orderedIds.Add(stageId);
            previousStage = parsed;
        }

        foreach (int id in orderedIds)
        {
            StageSpec stage = stagesById[id];
            if (TryParseEditStageParent(stage.ApplyAfter, out int parentId))
            {
                stagesById[parentId].Children.Add(stage);
            }
        }

        return [.. orderedIds.Select(id => stagesById[id])];
    }

    private static StageSpec ParseStage0(
        WorkflowGenerator g,
        StageDefaults baseDefaults,
        StageDefaults refinerDefaults,
        bool hasRefinerPhaseWork)
    {
        string applyAfterRaw = g.UserInput.Get(Base2EditExtension.ApplyEditAfter);
        string applyAfter = StringUtils.Equals(applyAfterRaw, ApplyAfterBase)
                || StringUtils.Equals(applyAfterRaw, ApplyAfterRefiner)
            ? applyAfterRaw
            : DefaultApplyAfter;
        if (!hasRefinerPhaseWork && StringUtils.Equals(applyAfter, ApplyAfterRefiner))
        {
            applyAfter = ApplyAfterBase;
        }

        StageDefaults inherited = StringUtils.Equals(applyAfter, ApplyAfterRefiner)
            ? refinerDefaults
            : baseDefaults;

        g.UserInput.TryGet(Base2EditExtension.EditModel, out string editModel);
        string resolvedModel = editModel ?? inherited.Model;

        g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel vaeModel);
        string resolvedVae = vaeModel?.Name ?? inherited.Vae;
        bool hasVaeOverride = vaeModel is not null && !string.IsNullOrWhiteSpace(vaeModel.Name);

        double upscale = g.UserInput.TryGet(Base2EditExtension.EditUpscale, out double upscaleRaw)
            ? upscaleRaw
            : inherited.Upscale;
        string upscaleMethod = g.UserInput.TryGet(Base2EditExtension.EditUpscaleMethod, out string upscaleMethodRaw)
            ? upscaleMethodRaw
            : inherited.UpscaleMethod;
        int steps = g.UserInput.TryGet(Base2EditExtension.EditSteps, out int stepsRaw)
            ? stepsRaw
            : inherited.Steps;

        StageDefaults paramDefaults = StringUtils.Equals(resolvedModel, ModelPrep.UseRefiner)
            ? refinerDefaults
            : baseDefaults;
        double cfgScale = g.UserInput.TryGet(Base2EditExtension.EditCFGScale, out double cfgRaw)
            ? cfgRaw
            : paramDefaults.CfgScale;
        string sampler = g.UserInput.TryGet(Base2EditExtension.EditSampler, out string samplerRaw)
            ? samplerRaw
            : paramDefaults.Sampler;
        string scheduler = g.UserInput.TryGet(Base2EditExtension.EditScheduler, out string schedulerRaw)
            ? schedulerRaw
            : paramDefaults.Scheduler;

        return new StageSpec(
            Id: 0,
            ApplyAfter: applyAfter,
            KeepPreEditImage: g.UserInput.Get(Base2EditExtension.KeepPreEditImage),
            RefineOnly: g.UserInput.Get(Base2EditExtension.EditRefineOnly),
            Control: NormalizeControl(g.UserInput.Get(Base2EditExtension.EditControl)),
            Model: resolvedModel,
            Vae: resolvedVae,
            Upscale: NormalizeUpscale(upscale),
            UpscaleMethod: upscaleMethod,
            Steps: steps,
            CfgScale: NormalizeCfgScale(cfgScale),
            Sampler: sampler,
            Scheduler: scheduler,
            HasVaeOverride: hasVaeOverride,
            Children: []
        );
    }

    private static StageSpec ParseJsonStage(
        JObject obj,
        int stageId,
        StageSpec previousStage,
        StageDefaults baseDefaults,
        StageDefaults refinerDefaults,
        bool hasRefinerPhaseWork,
        Dictionary<int, StageSpec> stagesById,
        Dictionary<int, StageDefaults> defaultsById)
    {
        string locationPrefix = $"Edit Stage {stageId}";

        string defaultApplyAfter = StageRefStore.FormatStageLabel(previousStage.Id);
        string applyAfterRaw = GetOptionalString(obj, "ApplyAfter", defaultApplyAfter, locationPrefix, allowEmpty: false);
        string normalizedApplyAfter = NormalizeApplyAfter(applyAfterRaw, stageId);
        if (normalizedApplyAfter is null)
        {
            return null;
        }
        if (!hasRefinerPhaseWork && StringUtils.Equals(normalizedApplyAfter, ApplyAfterRefiner))
        {
            normalizedApplyAfter = ApplyAfterBase;
        }

        if (TryParseEditStageParent(normalizedApplyAfter, out int parentId))
        {
            if (parentId >= stageId)
            {
                Logs.Warning($"Base2Edit: Edit Stage {stageId} cannot Apply After "
                    + $"'{StageRefStore.FormatStageLabel(parentId)}' (must reference an earlier stage).");
                return null;
            }
            if (!stagesById.ContainsKey(parentId))
            {
                Logs.Warning($"Base2Edit: Edit Stage {stageId} cannot Apply After "
                    + $"'{StageRefStore.FormatStageLabel(parentId)}' because that stage is missing or invalid.");
                return null;
            }
        }

        StageDefaults inherited = baseDefaults;
        if (StringUtils.Equals(normalizedApplyAfter, ApplyAfterRefiner))
        {
            inherited = refinerDefaults;
        }
        else if (TryParseEditStageParent(normalizedApplyAfter, out int depId))
        {
            inherited = defaultsById[depId];
        }

        string resolvedModel = GetOptionalString(obj, "Model", inherited.Model, locationPrefix, allowEmpty: false);

        bool vaeKeyPresent = JsonHasOwnProperty(obj, "Vae");
        string vaeRaw = GetString(obj, "Vae");
        string vaeTrimmed = string.IsNullOrWhiteSpace(vaeRaw) ? null : vaeRaw.Trim();
        string resolvedVae = vaeTrimmed ?? inherited.Vae;
        bool hasVaeOverride = vaeKeyPresent && vaeTrimmed is not null;

        StageDefaults paramDefaults = StringUtils.Equals(resolvedModel, ModelPrep.UseRefiner)
            ? refinerDefaults
            : baseDefaults;

        return new StageSpec(
            Id: stageId,
            ApplyAfter: normalizedApplyAfter,
            KeepPreEditImage: GetOptionalBool(obj, "KeepPreEditImage", previousStage.KeepPreEditImage),
            RefineOnly: GetOptionalBool(obj, "RefineOnly", previousStage.RefineOnly),
            Control: NormalizeControl(GetOptionalDouble(obj, "Control", previousStage.Control, locationPrefix)),
            Model: resolvedModel,
            Vae: resolvedVae,
            Upscale: NormalizeUpscale(GetOptionalDouble(obj, "Upscale", inherited.Upscale, locationPrefix)),
            UpscaleMethod: GetOptionalString(obj, "UpscaleMethod", inherited.UpscaleMethod, locationPrefix, allowEmpty: false),
            Steps: GetOptionalInt(obj, "Steps", inherited.Steps, locationPrefix),
            CfgScale: NormalizeCfgScale(GetOptionalDouble(obj, "CfgScale", paramDefaults.CfgScale, locationPrefix)),
            Sampler: GetOptionalString(obj, "Sampler", paramDefaults.Sampler, locationPrefix, allowEmpty: false),
            Scheduler: GetOptionalString(obj, "Scheduler", paramDefaults.Scheduler, locationPrefix, allowEmpty: false),
            HasVaeOverride: hasVaeOverride,
            Children: []
        );
    }

    private static StageDefaults ToStageDefaults(StageSpec stage) =>
        new(
            Model: stage.Model,
            Vae: stage.Vae,
            Upscale: stage.Upscale,
            UpscaleMethod: stage.UpscaleMethod,
            Steps: stage.Steps,
            CfgScale: stage.CfgScale,
            Sampler: stage.Sampler,
            Scheduler: stage.Scheduler
        );

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

    private static StageDefaults ResolveBaseDefaults(WorkflowGenerator g)
    {
        string model = g.UserInput.Get(T2IParamTypes.Model, null)?.Name ?? ModelPrep.UseBase;
        string vae = g.UserInput.Get(T2IParamTypes.VAE, null)?.Name ?? DefaultVae;
        double upscale = g.UserInput.Get(Base2EditExtension.EditUpscale, DefaultUpscale);
        string upscaleMethod = g.UserInput.Get(Base2EditExtension.EditUpscaleMethod, DefaultUpscaleMethod);
        int steps = g.UserInput.Get(T2IParamTypes.Steps, DefaultSteps);
        double cfgScale = g.UserInput.Get(T2IParamTypes.CFGScale, DefaultCfgScale);
        string sampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, DefaultSampler);
        string scheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, DefaultScheduler);

        return new StageDefaults(model, vae, upscale, upscaleMethod, steps, cfgScale, sampler, scheduler);
    }

    private static StageDefaults ResolveRefinerDefaults(WorkflowGenerator g, StageDefaults baseDefaults)
    {
        int refinerSectionId = T2IParamInput.SectionID_Refiner;
        string model = g.UserInput.Get(T2IParamTypes.RefinerModel, null)?.Name ?? baseDefaults.Model;
        string vae = g.UserInput.Get(T2IParamTypes.RefinerVAE, null)?.Name ?? baseDefaults.Vae;
        double upscale = g.UserInput.Get(T2IParamTypes.RefinerUpscale, baseDefaults.Upscale);
        string upscaleMethod = ComfyUIBackendExtension.RefinerUpscaleMethod is null
            ? baseDefaults.UpscaleMethod
            : g.UserInput.Get(ComfyUIBackendExtension.RefinerUpscaleMethod, baseDefaults.UpscaleMethod);
        int steps = g.UserInput.Get(T2IParamTypes.RefinerSteps, baseDefaults.Steps, sectionId: refinerSectionId);
        double cfgScale = g.UserInput.Get(
            T2IParamTypes.RefinerCFGScale,
            g.UserInput.Get(T2IParamTypes.CFGScale, baseDefaults.CfgScale, sectionId: refinerSectionId),
            sectionId: T2IParamInput.SectionID_Refiner
        );
        string sampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: refinerSectionId, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSamplerParam, null)
            ?? baseDefaults.Sampler;
        string scheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: refinerSectionId, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSchedulerParam, null)
            ?? baseDefaults.Scheduler;

        return new StageDefaults(model, vae, upscale, upscaleMethod, steps, cfgScale, sampler, scheduler);
    }

    private static string NormalizeApplyAfter(string applyAfterRaw, int stageId)
    {
        string applyAfter = string.IsNullOrWhiteSpace(applyAfterRaw) ? DefaultApplyAfter : applyAfterRaw.Trim();
        if (StringUtils.Equals(applyAfter, ApplyAfterBase))
        {
            return ApplyAfterBase;
        }

        if (StringUtils.Equals(applyAfter, ApplyAfterRefiner))
        {
            return ApplyAfterRefiner;
        }

        if (TryParseEditStageParent(applyAfter, out int parentId))
        {
            return StageRefStore.FormatStageLabel(parentId);
        }

        Logs.Warning($"Base2Edit: Edit Stage {stageId} has invalid Apply After '{applyAfterRaw}'.");
        return null;
    }

    private static bool TryParseEditStageParent(string applyAfter, out int parentId) =>
        StageRefStore.TryParseStageIndexKey(applyAfter, out parentId);

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
}
