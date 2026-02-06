using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public partial class EditStage
{
    private sealed record JsonStageSpec(
        int? Id,
        string ApplyAfter,
        bool? KeepPreEditImage,
        double? Control,
        string Model,
        string Vae,
        int? Steps,
        double? CfgScale,
        string Sampler,
        string Scheduler
    );

    private sealed record StageSpec(
        int Id,
        string ApplyAfter,
        bool? KeepPreEditImage,
        double? Control,
        string Model,
        string Vae,
        int? Steps,
        double? CfgScale,
        string Sampler,
        string Scheduler
    );

    private enum StageHook
    {
        Base,
        Refiner
    }

    private sealed record ResolvedStage(StageSpec Spec, StageHook Hook, int? DependsOnStageId);

    private static List<StageSpec> BuildUnifiedStages(WorkflowGenerator g, List<JsonStageSpec> jsonStages)
    {
        static StageSpec toStage(JsonStageSpec s, int id) => new(
            Id: id,
            ApplyAfter: s.ApplyAfter,
            KeepPreEditImage: s.KeepPreEditImage,
            Control: s.Control,
            Model: s.Model,
            Vae: s.Vae,
            Steps: s.Steps,
            CfgScale: s.CfgScale,
            Sampler: s.Sampler,
            Scheduler: s.Scheduler
        );

        List<JsonStageSpec> additional = [.. jsonStages ?? []];
        List<StageSpec> others = [.. additional.Select((s, idx) => toStage(s, idx + 1))];

        return [BuildStage0(g), .. others];
    }

    private static StageSpec BuildStage0(WorkflowGenerator g)
    {
        // Use the existing root-level params as "Edit Stage 0".
        // This keeps single-stage and multi-stage behavior consistent and makes stage0 the canonical source of truth.
        double? cfg = g.UserInput.TryGet(Base2EditExtension.EditCFGScale, out double c) ? c : null;
        string sampler = g.UserInput.TryGet(Base2EditExtension.EditSampler, out string s) ? s : null;
        string scheduler = g.UserInput.TryGet(Base2EditExtension.EditScheduler, out string sch) ? sch : null;
        string vae = g.UserInput.TryGet(Base2EditExtension.EditVAE, out T2IModel v) ? v?.Name : null;

        return new StageSpec(
            Id: 0,
            ApplyAfter: g.UserInput.Get(Base2EditExtension.ApplyEditAfter),
            KeepPreEditImage: g.UserInput.Get(Base2EditExtension.KeepPreEditImage),
            Control: g.UserInput.Get(Base2EditExtension.EditControl),
            Model: g.UserInput.Get(Base2EditExtension.EditModel),
            Steps: g.UserInput.Get(Base2EditExtension.EditSteps),
            Vae: vae,
            CfgScale: cfg,
            Sampler: sampler,
            Scheduler: scheduler
        );
    }

    private static bool TryGetEditStages(WorkflowGenerator g, out List<JsonStageSpec> stages)
    {
        stages = [];
        if (g?.UserInput is null)
        {
            return false;
        }

        if (!g.UserInput.TryGet(Base2EditExtension.EditStages, out string json) || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            JToken token = JToken.Parse(json);
            if (token is not JArray arr)
            {
                return false;
            }

            foreach (JToken item in arr)
            {
                if (item is not JObject obj)
                {
                    continue;
                }

                string getStr(string key)
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

                int? getIntRaw(string key)
                {
                    string s = getStr(key);
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

                bool? getBool(string key)
                {
                    string s = getStr(key);
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

                int? getInt(string key)
                {
                    string s = getStr(key);
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

                double? getDouble(string key)
                {
                    string s = getStr(key);
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

                stages.Add(new JsonStageSpec(
                    Id: getIntRaw("id"),
                    ApplyAfter: getStr("applyAfter"),
                    KeepPreEditImage: getBool("keepPreEditImage"),
                    Control: getDouble("control"),
                    Model: getStr("model"),
                    Vae: getStr("vae"),
                    Steps: getInt("steps"),
                    CfgScale: getDouble("cfgScale"),
                    Sampler: getStr("sampler"),
                    Scheduler: getStr("scheduler")
                ));
            }
        }
        catch
        {
            stages = [];
            return false;
        }

        return true;
    }

    private static List<ResolvedStage> ResolveStages(List<StageSpec> stages)
    {
        Dictionary<int, StageSpec> byId = new();
        foreach (StageSpec st in stages)
        {
            if (st.Id < 0)
            {
                throw new SwarmReadableErrorException($"Base2Edit: stage has invalid id '{st.Id}'.");
            }

            if (!byId.TryAdd(st.Id, st))
            {
                throw new SwarmReadableErrorException($"Base2Edit: duplicate stage id '{st.Id}'.");
            }
        }

        Dictionary<int, ResolvedStage> memo = new();

        (StageHook Hook, int? DependsOn) resolve(int id, HashSet<int> stack)
        {
            if (memo.TryGetValue(id, out ResolvedStage cached))
            {
                return (cached.Hook, cached.DependsOnStageId);
            }

            if (!byId.TryGetValue(id, out StageSpec st))
            {
                throw new SwarmReadableErrorException($"Base2Edit: missing Edit Stage {id}.");
            }

            if (!stack.Add(id))
            {
                throw new SwarmReadableErrorException($"Base2Edit: cycle detected involving Edit Stage {id}.");
            }

            string applyAfter = string.IsNullOrWhiteSpace(st.ApplyAfter) ? "Refiner" : st.ApplyAfter.Trim();
            if (string.Equals(applyAfter, "Base", StringComparison.OrdinalIgnoreCase))
            {
                stack.Remove(id);
                return (StageHook.Base, null);
            }

            if (string.Equals(applyAfter, "Refiner", StringComparison.OrdinalIgnoreCase))
            {
                stack.Remove(id);
                return (StageHook.Refiner, null);
            }

            if (applyAfter.StartsWith("Edit Stage ", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = applyAfter["Edit Stage ".Length..].Trim();
                if (!int.TryParse(suffix, out int depId))
                {
                    throw new SwarmReadableErrorException($"Base2Edit: Edit Stage {id} has invalid Apply After '{st.ApplyAfter}'.");
                }

                if (depId >= id)
                {
                    throw new SwarmReadableErrorException($"Base2Edit: Edit Stage {id} cannot Apply After 'Edit Stage {depId}' (must reference an earlier stage).");
                }
                // Intentionally allow missing dependency stages.
                // This lets users remove stages even if another stage still references them.
                // Fallback behavior: treat missing dependency as "Refiner" with no dependency.
                if (!byId.ContainsKey(depId))
                {
                    stack.Remove(id);
                    return (StageHook.Refiner, null);
                }
                (StageHook depHook, _) = resolve(depId, stack);
                stack.Remove(id);
                return (depHook, depId);
            }

            throw new SwarmReadableErrorException($"Base2Edit: Edit Stage {id} has invalid Apply After '{st.ApplyAfter}'.");
        }

        foreach (int id in byId.Keys.OrderBy(i => i))
        {
            (StageHook hook, int? dep) = resolve(id, []);
            ResolvedStage resolved = new(byId[id], hook, dep);
            memo[id] = resolved;
        }

        return memo.Values.ToList();
    }
}
