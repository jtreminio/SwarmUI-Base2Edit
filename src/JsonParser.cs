using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public class JsonParser(WorkflowGenerator g)
{
    private sealed record RawStageSpec(
        int Id,
        string ApplyAfter,
        bool KeepPreEditImage,
        bool RefineOnly,
        double Control,
        string Model,
        string Vae,
        int? Steps,
        double? CfgScale,
        string Sampler,
        string Scheduler
    );

    private sealed record StageDefaults(
        string Model,
        string Vae,
        int Steps,
        double CfgScale,
        string Sampler,
        string Scheduler
    );

    public sealed record StageSpec(
        int Id,
        string ApplyAfter,
        bool KeepPreEditImage,
        bool RefineOnly,
        double Control,
        string Model,
        string Vae,
        int Steps,
        double CfgScale,
        string Sampler,
        string Scheduler,
        bool HasVaeOverride,
        List<StageSpec> Children = default!
    );

    public List<StageSpec> ParseEditStages()
    {
        List<object> jsonStages = GetJsonStagesArray();
        List<RawStageSpec> rawStages = [];
        int nextId = 1;

        string stage0ApplyAfter = g.UserInput.Get(Base2EditExtension.ApplyEditAfter);
        if (!string.Equals(stage0ApplyAfter, "Base", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stage0ApplyAfter, "Refiner", StringComparison.OrdinalIgnoreCase))
        {
            Logs.Warning($"Base2Edit: invalid Apply After '{stage0ApplyAfter}', setting to 'Refiner'.");
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
            Steps: g.UserInput.TryGet(Base2EditExtension.EditSteps, out int stage0Steps) ? stage0Steps : null,
            CfgScale: g.UserInput.TryGet(Base2EditExtension.EditCFGScale, out double stage0Cfg) ? stage0Cfg : null,
            Sampler: g.UserInput.TryGet(Base2EditExtension.EditSampler, out string stage0Sampler) ? stage0Sampler : null,
            Scheduler: g.UserInput.TryGet(Base2EditExtension.EditScheduler, out string stage0Scheduler) ? stage0Scheduler : null
        ));

        RawStageSpec previousStage = rawStages[0];

        foreach (object raw in jsonStages ?? [])
        {
            if (raw is not JObject obj)
            {
                continue;
            }

            RawStageSpec stage = new(
                Id: nextId++,
                ApplyAfter: GetStr("ApplyAfter", obj) ?? $"Edit Stage {previousStage.Id}",
                KeepPreEditImage: GetBool("KeepPreEditImage", obj) ?? previousStage.KeepPreEditImage,
                RefineOnly: GetBool("RefineOnly", obj) ?? previousStage.RefineOnly,
                Control: GetDouble("Control", obj) ?? previousStage.Control,
                Model: GetStr("Model", obj),
                Vae: GetStr("Vae", obj),
                Steps: GetInt("Steps", obj),
                CfgScale: GetDouble("CfgScale", obj),
                Sampler: GetStr("Sampler", obj),
                Scheduler: GetStr("Scheduler", obj)
            );
            rawStages.Add(stage);
            previousStage = stage;
        }

        Dictionary<int, StageSpec> stagesById = [];
        Dictionary<int, StageDefaults> resolvedDefaultsById = [];
        List<int> validOrderedIds = [];
        StageDefaults baseDefaults = ResolveBaseDefaults();
        StageDefaults refinerDefaults = ResolveRefinerDefaults(baseDefaults);

        foreach (RawStageSpec stage in rawStages.OrderBy(s => s.Id))
        {
            string normalizedApplyAfter = NormalizeApplyAfter(stage.ApplyAfter, stage.Id);
            if (normalizedApplyAfter is null)
            {
                continue;
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
            if (string.Equals(normalizedApplyAfter, "Refiner"))
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
            StageDefaults paramDefaults;
            if (string.Equals(resolvedModel, ModelPrep.UseRefiner, StringComparison.OrdinalIgnoreCase))
                paramDefaults = refinerDefaults;
            else
                paramDefaults = baseDefaults;

            StageSpec resolved = new(
                Id: stage.Id,
                ApplyAfter: normalizedApplyAfter,
                KeepPreEditImage: stage.KeepPreEditImage,
                RefineOnly: stage.RefineOnly,
                Control: stage.Control,
                Model: resolvedModel,
                Vae: PickStringOrDefault(stage.Vae, inherited.Vae),
                Steps: stage.Steps ?? inherited.Steps,
                CfgScale: stage.CfgScale ?? paramDefaults.CfgScale,
                Sampler: PickStringOrDefault(stage.Sampler, paramDefaults.Sampler),
                Scheduler: PickStringOrDefault(stage.Scheduler, paramDefaults.Scheduler),
                HasVaeOverride: stage.Vae is not null,
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

    private List<object> GetJsonStagesArray()
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

            return [.. arr.Select(item => item.ToObject<object>())];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private StageDefaults ResolveBaseDefaults()
    {
        string model = g.UserInput.Get(T2IParamTypes.Model, null)?.Name ?? ModelPrep.UseBase;
        string vae = g.UserInput.Get(T2IParamTypes.VAE, null)?.Name ?? "None";
        int steps = g.UserInput.Get(T2IParamTypes.Steps, 20);
        double cfgScale = g.UserInput.Get(T2IParamTypes.CFGScale, 7);
        string sampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler");
        string scheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "normal");

        return new StageDefaults(model, vae, steps, cfgScale, sampler, scheduler);
    }

    private StageDefaults ResolveRefinerDefaults(StageDefaults baseDefaults)
    {
        int refinerSectionId = T2IParamInput.SectionID_Refiner;
        string model = g.UserInput.Get(T2IParamTypes.RefinerModel, null)?.Name ?? baseDefaults.Model;
        string vae = g.UserInput.Get(T2IParamTypes.RefinerVAE, null)?.Name ?? baseDefaults.Vae;
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

        return new StageDefaults(model, vae, steps, cfgScale, sampler, scheduler);
    }

    private static string PickStringOrDefault(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeApplyAfter(string applyAfterRaw, int stageId)
    {
        string applyAfter = string.IsNullOrWhiteSpace(applyAfterRaw) ? "Refiner" : applyAfterRaw.Trim();
        if (string.Equals(applyAfter, "Base", StringComparison.OrdinalIgnoreCase))
        {
            return "Base";
        }

        if (string.Equals(applyAfter, "Refiner", StringComparison.OrdinalIgnoreCase))
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

    private static string GetStr(string key, JObject obj)
    {
        foreach (JProperty p in obj.Properties())
        {
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value?.Type == JTokenType.Null ? null : $"{p.Value}";
            }
        }
        return null;
    }

    private static bool? GetBool(string key, JObject obj)
    {
        string s = GetStr(key, obj);
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        if (bool.TryParse(s, out bool b))
        {
            return b;
        }
        return null;
    }

    private static int? GetInt(string key, JObject obj)
    {
        string s = GetStr(key, obj);
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        if (int.TryParse(s, out int i))
        {
            return i;
        }
        return null;
    }

    private static double? GetDouble(string key, JObject obj)
    {
        string s = GetStr(key, obj);
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        if (double.TryParse(s, out double d))
        {
            return d;
        }
        return null;
    }
}
