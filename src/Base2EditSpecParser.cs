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

    private sealed record RawStageSpec(
        int Id,
        string ApplyAfter,
        bool KeepPreEditImage,
        bool RefineOnly,
        double Control,
        string Model,
        string Vae,
        bool VaeKeyPresent,
        double? Upscale,
        string UpscaleMethod,
        int? Steps,
        double? CfgScale,
        string Sampler,
        string Scheduler
    );

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

        List<RawStageSpec> rawStages = [BuildStage0Raw(g)];
        rawStages.AddRange(BuildJsonStagesRaw(GetJsonStagesArray(g), rawStages[0]));

        return ResolveStages(rawStages, baseDefaults, refinerDefaults, hasRefinerPhaseWork);
    }

    private static RawStageSpec BuildStage0Raw(WorkflowGenerator g)
    {
        string applyAfterRaw = g.UserInput.Get(Base2EditExtension.ApplyEditAfter);
        string applyAfter = StringUtils.Equals(applyAfterRaw, ApplyAfterBase)
                || StringUtils.Equals(applyAfterRaw, ApplyAfterRefiner)
            ? applyAfterRaw
            : DefaultApplyAfter;

        g.UserInput.TryGet(Base2EditExtension.EditModel, out string model);
        g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel vaeModel);

        return new RawStageSpec(
            Id: 0,
            ApplyAfter: applyAfter,
            KeepPreEditImage: g.UserInput.Get(Base2EditExtension.KeepPreEditImage),
            RefineOnly: g.UserInput.Get(Base2EditExtension.EditRefineOnly),
            Control: g.UserInput.Get(Base2EditExtension.EditControl),
            Model: model,
            Vae: vaeModel?.Name,
            VaeKeyPresent: vaeModel is not null,
            Upscale: g.UserInput.TryGet(Base2EditExtension.EditUpscale, out double upscale) ? upscale : null,
            UpscaleMethod: g.UserInput.TryGet(Base2EditExtension.EditUpscaleMethod, out string upscaleMethod) ? upscaleMethod : null,
            Steps: g.UserInput.TryGet(Base2EditExtension.EditSteps, out int steps) ? steps : null,
            CfgScale: g.UserInput.TryGet(Base2EditExtension.EditCFGScale, out double cfgScale) ? cfgScale : null,
            Sampler: g.UserInput.TryGet(Base2EditExtension.EditSampler, out string sampler) ? sampler : null,
            Scheduler: g.UserInput.TryGet(Base2EditExtension.EditScheduler, out string scheduler) ? scheduler : null
        );
    }

    private static IEnumerable<RawStageSpec> BuildJsonStagesRaw(List<JObject> jsonStages, RawStageSpec stage0)
    {
        List<RawStageSpec> result = [];
        int nextId = 1;
        RawStageSpec previousStage = stage0;

        foreach (JObject obj in jsonStages)
        {
            int stageId = nextId++;
            if (GetOptionalBool(obj, "Skipped", false))
            {
                continue;
            }
            RawStageSpec stage = new(
                Id: stageId,
                ApplyAfter: GetOptionalNullableString(obj, "ApplyAfter") ?? StageRefStore.FormatStageLabel(previousStage.Id),
                KeepPreEditImage: GetOptionalNullableBool(obj, "KeepPreEditImage") ?? previousStage.KeepPreEditImage,
                RefineOnly: GetOptionalNullableBool(obj, "RefineOnly") ?? previousStage.RefineOnly,
                Control: GetOptionalNullableDouble(obj, "Control") ?? previousStage.Control,
                Model: GetOptionalNullableString(obj, "Model"),
                Vae: GetOptionalNullableString(obj, "Vae"),
                VaeKeyPresent: JsonHasOwnProperty(obj, "Vae"),
                Upscale: GetOptionalNullableDouble(obj, "Upscale"),
                UpscaleMethod: GetOptionalNullableString(obj, "UpscaleMethod"),
                Steps: GetOptionalNullableInt(obj, "Steps"),
                CfgScale: GetOptionalNullableDouble(obj, "CfgScale"),
                Sampler: GetOptionalNullableString(obj, "Sampler"),
                Scheduler: GetOptionalNullableString(obj, "Scheduler")
            );
            result.Add(stage);
            previousStage = stage;
        }

        return result;
    }

    private static List<StageSpec> ResolveStages(
        List<RawStageSpec> rawStages,
        StageDefaults baseDefaults,
        StageDefaults refinerDefaults,
        bool hasRefinerPhaseWork)
    {
        Dictionary<int, StageSpec> stagesById = [];
        Dictionary<int, StageDefaults> resolvedDefaultsById = [];
        List<int> validOrderedIds = [];

        foreach (RawStageSpec stage in rawStages.OrderBy(s => s.Id))
        {
            string normalizedApplyAfter = NormalizeApplyAfter(stage.ApplyAfter, stage.Id);
            if (normalizedApplyAfter is null)
            {
                continue;
            }
            if (!hasRefinerPhaseWork && StringUtils.Equals(normalizedApplyAfter, ApplyAfterRefiner))
            {
                normalizedApplyAfter = ApplyAfterBase;
            }

            if (TryParseEditStageParent(normalizedApplyAfter, out int parentId))
            {
                if (parentId >= stage.Id)
                {
                    Logs.Warning($"Base2Edit: Edit Stage {stage.Id} cannot Apply After "
                        + $"'{StageRefStore.FormatStageLabel(parentId)}' (must reference an earlier stage).");
                    continue;
                }

                if (!stagesById.ContainsKey(parentId))
                {
                    Logs.Warning($"Base2Edit: Edit Stage {stage.Id} cannot Apply After "
                        + $"'{StageRefStore.FormatStageLabel(parentId)}' because that stage is missing or invalid.");
                    continue;
                }
            }

            StageDefaults inherited = baseDefaults;
            if (StringUtils.Equals(normalizedApplyAfter, ApplyAfterRefiner))
            {
                inherited = refinerDefaults;
            }
            else if (TryParseEditStageParent(normalizedApplyAfter, out int depId))
            {
                inherited = resolvedDefaultsById[depId];
            }

            // Resolve the model first so we know which defaults to use for CFG/Sampler/Scheduler
            string resolvedModel = stage.Model ?? inherited.Model;

            // For CFG/Sampler/Scheduler, always resolve from global base/refiner defaults based
            // on the model, not from the parent stage.
            StageDefaults paramDefaults = StringUtils.Equals(resolvedModel, ModelPrep.UseRefiner)
                ? refinerDefaults
                : baseDefaults;

            StageSpec resolved = new(
                Id: stage.Id,
                ApplyAfter: normalizedApplyAfter,
                KeepPreEditImage: stage.KeepPreEditImage,
                RefineOnly: stage.RefineOnly,
                Control: NormalizeControl(stage.Control),
                Model: resolvedModel,
                Vae: stage.Vae ?? inherited.Vae,
                Upscale: NormalizeUpscale(stage.Upscale ?? inherited.Upscale),
                UpscaleMethod: stage.UpscaleMethod ?? inherited.UpscaleMethod,
                Steps: stage.Steps ?? inherited.Steps,
                CfgScale: NormalizeCfgScale(stage.CfgScale ?? paramDefaults.CfgScale),
                Sampler: stage.Sampler ?? paramDefaults.Sampler,
                Scheduler: stage.Scheduler ?? paramDefaults.Scheduler,
                HasVaeOverride: stage.VaeKeyPresent && !string.IsNullOrWhiteSpace(stage.Vae),
                Children: []
            );

            if (!stagesById.TryAdd(stage.Id, resolved))
            {
                Logs.Warning($"Base2Edit: duplicate stage id '{stage.Id}'.");
                continue;
            }

            resolvedDefaultsById[stage.Id] = new StageDefaults(
                Model: resolved.Model,
                Vae: resolved.Vae,
                Upscale: resolved.Upscale,
                UpscaleMethod: resolved.UpscaleMethod,
                Steps: resolved.Steps,
                CfgScale: resolved.CfgScale,
                Sampler: resolved.Sampler,
                Scheduler: resolved.Scheduler
            );
            validOrderedIds.Add(stage.Id);
        }

        foreach (int stageId in validOrderedIds)
        {
            StageSpec stage = stagesById[stageId];
            if (!TryParseEditStageParent(stage.ApplyAfter, out int parentId))
            {
                continue;
            }

            stagesById[parentId].Children.Add(stage);
        }

        return [.. validOrderedIds.Select(id => stagesById[id])];
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

    private static string GetOptionalNullableString(JObject obj, string key)
    {
        string value = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return value.Trim();
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

    private static bool? GetOptionalNullableBool(JObject obj, string key)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return bool.TryParse(raw.Trim(), out bool value) ? value : null;
    }

    private static int? GetOptionalNullableInt(JObject obj, string key)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return int.TryParse(raw.Trim(), out int value) ? value : null;
    }

    private static double? GetOptionalNullableDouble(JObject obj, string key)
    {
        string raw = GetString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return double.TryParse(raw.Trim(), out double value) ? value : null;
    }

    private static bool JsonHasOwnProperty(JObject obj, string key) =>
        obj.Properties().Any(p => StringUtils.Equals(p.Name, key));
}
