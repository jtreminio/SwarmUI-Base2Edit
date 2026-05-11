using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

internal static class Base2EditSpecParser
{
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

    public static List<StageSpec> ParseEditStages(WorkflowGenerator g)
    {
        List<JObject> jsonStages = GetJsonStagesArray(g);
        List<RawStageSpec> rawStages = [];
        int nextId = 1;
        bool hasRefinerStageConfigured = HasRefinerStageConfigured(g);
        bool hasRefinerPhaseWork = hasRefinerStageConfigured || HasSegmentApplyAfterRefiner(g);
        string stage0ApplyAfter = g.UserInput.Get(Base2EditExtension.ApplyEditAfter);
        if (!StringUtils.Equals(stage0ApplyAfter, "Base")
            && !StringUtils.Equals(stage0ApplyAfter, "Refiner"))
        {
            stage0ApplyAfter = "Refiner";
        }

        g.UserInput.TryGet(Base2EditExtension.EditModel, out string stage0Model);
        g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel stage0VaeModel);

        rawStages.Add(new(
            Id: 0,
            ApplyAfter: stage0ApplyAfter,
            KeepPreEditImage: g.UserInput.Get(Base2EditExtension.KeepPreEditImage),
            RefineOnly: g.UserInput.Get(Base2EditExtension.EditRefineOnly),
            Control: g.UserInput.Get(Base2EditExtension.EditControl),
            Model: stage0Model,
            Vae: stage0VaeModel?.Name,
            VaeKeyPresent: stage0VaeModel is not null,
            Upscale: g.UserInput.TryGet(Base2EditExtension.EditUpscale, out double stage0Upscale) ? stage0Upscale : null,
            UpscaleMethod: g.UserInput.TryGet(Base2EditExtension.EditUpscaleMethod, out string stage0UpscaleMethod) ? stage0UpscaleMethod : null,
            Steps: g.UserInput.TryGet(Base2EditExtension.EditSteps, out int stage0Steps) ? stage0Steps : null,
            CfgScale: g.UserInput.TryGet(Base2EditExtension.EditCFGScale, out double stage0CfgScale) ? stage0CfgScale : null,
            Sampler: g.UserInput.TryGet(Base2EditExtension.EditSampler, out string stage0Sampler) ? stage0Sampler : null,
            Scheduler: g.UserInput.TryGet(Base2EditExtension.EditScheduler, out string stage0Scheduler) ? stage0Scheduler : null
        ));

        RawStageSpec previousStage = rawStages[0];

        foreach (JObject obj in jsonStages)
        {
            int stageId = nextId++;
            string locationPrefix = $"Edit Stage {stageId}";
            RawStageSpec stage = new(
                Id: stageId,
                ApplyAfter: GetNullableString(obj, "ApplyAfter") ?? $"Edit Stage {previousStage.Id}",
                KeepPreEditImage: GetOptionalNullableBool(obj, "KeepPreEditImage", locationPrefix) ?? previousStage.KeepPreEditImage,
                RefineOnly: GetOptionalNullableBool(obj, "RefineOnly", locationPrefix) ?? previousStage.RefineOnly,
                Control: GetOptionalNullableDouble(obj, "Control", locationPrefix) ?? previousStage.Control,
                Model: GetNullableString(obj, "Model"),
                Vae: GetNullableString(obj, "Vae"),
                VaeKeyPresent: JsonHasOwnProperty(obj, "Vae"),
                Upscale: GetOptionalNullableDouble(obj, "Upscale", locationPrefix),
                UpscaleMethod: GetNullableString(obj, "UpscaleMethod"),
                Steps: GetOptionalNullableInt(obj, "Steps", locationPrefix),
                CfgScale: GetOptionalNullableDouble(obj, "CfgScale", locationPrefix),
                Sampler: GetNullableString(obj, "Sampler"),
                Scheduler: GetNullableString(obj, "Scheduler")
            );
            rawStages.Add(stage);
            previousStage = stage;
        }

        Dictionary<int, StageSpec> stagesById = [];
        Dictionary<int, StageDefaults> resolvedDefaultsById = [];
        List<int> validOrderedIds = [];
        StageDefaults baseDefaults = ResolveBaseDefaults(g);
        StageDefaults refinerDefaults = ResolveRefinerDefaults(g, baseDefaults);

        foreach (RawStageSpec stage in rawStages.OrderBy(s => s.Id))
        {
            string normalizedApplyAfter = NormalizeApplyAfter(stage.ApplyAfter, stage.Id);
            if (normalizedApplyAfter is null)
            {
                continue;
            }
            if (!hasRefinerPhaseWork && StringUtils.Equals(normalizedApplyAfter, "Refiner"))
            {
                normalizedApplyAfter = "Base";
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
            if (StringUtils.Equals(normalizedApplyAfter, "Refiner"))
            {
                inherited = refinerDefaults;
            }
            else if (TryParseEditStageParent(normalizedApplyAfter, out int depId))
            {
                inherited = resolvedDefaultsById[depId];
            }

            // Resolve the model first so we know which defaults to use for CFG/Sampler/Scheduler
            string resolvedModel = PickStringOrDefault(stage.Model, inherited.Model);

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
                Vae: PickStringOrDefault(stage.Vae, inherited.Vae),
                Upscale: NormalizeUpscale(stage.Upscale ?? inherited.Upscale),
                UpscaleMethod: PickStringOrDefault(stage.UpscaleMethod, inherited.UpscaleMethod),
                Steps: stage.Steps ?? inherited.Steps,
                CfgScale: NormalizeCfgScale(stage.CfgScale ?? paramDefaults.CfgScale),
                Sampler: PickStringOrDefault(stage.Sampler, paramDefaults.Sampler),
                Scheduler: PickStringOrDefault(stage.Scheduler, paramDefaults.Scheduler),
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

        try
        {
            JToken token = JToken.Parse(json);
            if (token is not JArray arr)
            {
                return [];
            }

            return [.. arr.OfType<JObject>()];
        }
        catch (JsonException ex)
        {
            throw new SwarmUserErrorException(
                $"Base2Edit: Could not parse Edit Stages JSON. {ex.Message}");
        }
    }

    private static StageDefaults ResolveBaseDefaults(WorkflowGenerator g)
    {
        string model = g.UserInput.Get(T2IParamTypes.Model, null)?.Name ?? ModelPrep.UseBase;
        string vae = g.UserInput.Get(T2IParamTypes.VAE, null)?.Name ?? "None";
        double upscale = g.UserInput.Get(Base2EditExtension.EditUpscale, 1.0);
        string upscaleMethod = g.UserInput.Get(Base2EditExtension.EditUpscaleMethod, "pixel-lanczos");
        int steps = g.UserInput.Get(T2IParamTypes.Steps, 20);
        double cfgScale = g.UserInput.Get(T2IParamTypes.CFGScale, 7);
        string sampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler");
        string scheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "normal");

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

    private static string PickStringOrDefault(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeApplyAfter(string applyAfterRaw, int stageId)
    {
        string applyAfter = string.IsNullOrWhiteSpace(applyAfterRaw) ? "Refiner" : applyAfterRaw.Trim();
        if (StringUtils.Equals(applyAfter, "Base"))
        {
            return "Base";
        }

        if (StringUtils.Equals(applyAfter, "Refiner"))
        {
            return "Refiner";
        }

        if (TryParseEditStageParent(applyAfter, out int parentId))
        {
            if (stageId == 0)
            {
                Logs.Warning("Base2Edit: Edit Stage 0 must Apply After 'Base' or 'Refiner'. Falling back to 'Refiner'.");
                return "Refiner";
            }
            return StageRefStore.FormatStageLabel(parentId);
        }

        if (stageId == 0)
        {
            Logs.Warning($"Base2Edit: Edit Stage 0 has invalid Apply After '{applyAfterRaw}'. Falling back to 'Refiner'.");
            return "Refiner";
        }

        Logs.Warning($"Base2Edit: Edit Stage {stageId} has invalid Apply After '{applyAfterRaw}'.");
        return null;
    }

    private static bool TryParseEditStageParent(string applyAfter, out int parentId)
    {
        return StageRefStore.TryParseStageIndexKey(applyAfter, out parentId);
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
        string segmentApplyAfter = g.UserInput.Get(T2IParamTypes.SegmentApplyAfter, "Refiner");
        if (!StringUtils.Equals(segmentApplyAfter, "Refiner"))
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

    private static double NormalizeControl(double control) =>
        TruncateToDecimals(Math.Clamp(control, 0, 1), 2);

    private static double NormalizeUpscale(double upscale) =>
        TruncateToDecimals(upscale, 2);

    private static double NormalizeCfgScale(double cfgScale) =>
        TruncateToDecimals(cfgScale, 1);

    private static string GetNullableString(JObject obj, string key)
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

    private static bool? GetOptionalNullableBool(JObject obj, string key, string locationPrefix)
    {
        string raw = GetNullableString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (bool.TryParse(raw.Trim(), out bool value))
        {
            return value;
        }
        Logs.Warning(
            $"Base2Edit: {locationPrefix} has invalid boolean field '{key}' value '{raw}'. "
            + "Ignoring and falling back to inherited value.");
        return null;
    }

    private static int? GetOptionalNullableInt(JObject obj, string key, string locationPrefix)
    {
        string raw = GetNullableString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (int.TryParse(raw.Trim(), out int value))
        {
            return value;
        }
        Logs.Warning(
            $"Base2Edit: {locationPrefix} has invalid integer field '{key}' value '{raw}'. "
            + "Ignoring and falling back to inherited value.");
        return null;
    }

    private static double? GetOptionalNullableDouble(JObject obj, string key, string locationPrefix)
    {
        string raw = GetNullableString(obj, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (double.TryParse(raw.Trim(), out double value))
        {
            return value;
        }
        Logs.Warning(
            $"Base2Edit: {locationPrefix} has invalid numeric field '{key}' value '{raw}'. "
            + "Ignoring and falling back to inherited value.");
        return null;
    }

    private static bool JsonHasOwnProperty(JObject obj, string key) =>
        obj.Properties().Any(p => StringUtils.Equals(p.Name, key));
}
