using System.Runtime.CompilerServices;
using SwarmUI.Text2Image;

namespace Base2Edit;

internal static class EditPresets
{
    private sealed record DeferredPrompt(string Original, string Deferred);
    private static readonly ConditionalWeakTable<T2IParamInput, DeferredPrompt> DeferredPrompts = new();
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;
        foreach (string tag in new[] { "preset", "p" })
        {
            Func<string, T2IPromptHandling.PromptTagContext, string> original = T2IPromptHandling.PromptTagProcessors[tag];
            T2IPromptHandling.PromptTagProcessors[tag] = (data, context) =>
                context.SectionID >= Base2EditExtension.SectionID_Edit
                    ? Process(data, context)
                    : original(data, context);
        }

        // Core applies PendingPresets first. Protect the resulting prompt before
        // its early model scan, then restore the source for parsing and metadata.
        T2IParamInput.SpecialParameterHandlers.Insert(1, DeferEarlyPresets);
        T2IParamInput.SpecialParameterHandlers.Add(RestoreEarlyPresets);
    }

    private static void DeferEarlyPresets(T2IParamInput input)
    {
        if (input.EarlyLoadDone || !input.TryGet(T2IParamTypes.Prompt, out string prompt)
            || !prompt.Contains("<preset:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        string deferred = PromptParser.DeferEditPresets(prompt);
        if (deferred != prompt)
        {
            DeferredPrompts.AddOrUpdate(input, new(prompt, deferred));
            input.InternalSet.ValuesInput[T2IParamTypes.Prompt.Type.ID] = deferred;
        }
    }

    private static void RestoreEarlyPresets(T2IParamInput input)
    {
        if (DeferredPrompts.TryGetValue(input, out DeferredPrompt prompt))
        {
            DeferredPrompts.Remove(input);
            if (input.Get(T2IParamTypes.Prompt) == prompt.Deferred)
            {
                input.InternalSet.ValuesInput[T2IParamTypes.Prompt.Type.ID] = prompt.Original;
            }
        }
    }

    private static string Process(string data, T2IPromptHandling.PromptTagContext context)
    {
        string name = context.Parse(data);
        T2IPreset preset = context.Input.SourceSession.User.GetPreset(name);
        if (preset is null)
        {
            context.TrackWarning($"Preset '{name}' does not exist and will be ignored.");
            return null;
        }
        foreach ((string key, string value) in preset.ParamMap)
        {
            T2IParamType target = T2IParamTypes.CleanTypeName(key) switch
            {
                "model" => Base2EditExtension.EditModel.Type,
                "vae" => Base2EditExtension.EditVAE.Type,
                "steps" => Base2EditExtension.EditSteps.Type,
                "cfgscale" => Base2EditExtension.EditCFGScale.Type,
                "sampler" => Base2EditExtension.EditSampler.Type,
                "scheduler" => Base2EditExtension.EditScheduler.Type,
                _ => null
            };
            if (target is null && T2IParamTypes.TryGetType(key, out T2IParamType type, context.Input)
                && type.Group == Base2EditExtension.Base2EditGroup && type.CanSectionalize)
            {
                target = type;
            }
            if (target is not null)
            {
                T2IParamTypes.ApplyParameter(target.ID, value, context.Input, context.SectionID);
            }
        }

        ApplyLoras(preset, context);
        foreach (T2IRegisteredParam<string> param in new[] { T2IParamTypes.Prompt, T2IParamTypes.NegativePrompt })
        {
            if (preset.ParamMap.TryGetValue(param.Type.ID, out string template))
            {
                template = context.Parse(template);
                if (!template.Contains("{value}"))
                {
                    template = "{value} " + template;
                }
                string prior = context.Input.Get(param, "{value}", sectionId: context.SectionID, includeBase: false);
                // Preserve {value} until the stage's own prompt is available. Set()
                // would run the prompt cleaner and consume the placeholder now.
                context.Input.GetSectionParamOverrides(context.SectionID).ValuesInput[param.Type.ID]
                    = T2IParamTypes.ApplyStringEdit(prior, template);
            }
        }
        return "";
    }

    private static void ApplyLoras(T2IPreset preset, T2IPromptHandling.PromptTagContext context)
    {
        if (!preset.ParamMap.TryGetValue("loras", out string names) || string.IsNullOrWhiteSpace(names))
        {
            return;
        }
        string[] loras = names.Split(',');
        string[] weights = preset.ParamMap.GetValueOrDefault("loraweights", "").Split(',');
        string[] tencWeights = preset.ParamMap.GetValueOrDefault("loratencweights", "").Split(',');
        for (int i = 0; i < loras.Length; i++)
        {
            string weight = i < weights.Length && !string.IsNullOrWhiteSpace(weights[i]) ? weights[i].Trim() : "1";
            string tencWeight = i < tencWeights.Length && !string.IsNullOrWhiteSpace(tencWeights[i]) ? tencWeights[i].Trim() : weight;
            context.Parse($"<lora:{loras[i].Trim()}:{weight}:{tencWeight}>");
        }
    }

    public static string ApplyPrompt(T2IParamInput input, T2IRegisteredParam<string> param, int stageId, string prompt)
    {
        foreach (int section in new[] { Base2EditExtension.SectionID_Edit, Base2EditExtension.EditSectionIdForStage(stageId) })
        {
            if (input.TryGet(param, out string template, section, includeBase: false))
            {
                prompt = T2IParamTypes.ApplyStringEdit(prompt, template);
            }
        }
        return prompt.Trim();
    }
}
