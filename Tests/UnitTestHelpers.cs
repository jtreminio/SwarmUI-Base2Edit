using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.WebAPI;

namespace Base2Edit.Tests;

internal static class UnitTestStubs
{
    /// <summary>
    /// Some WorkflowGenerator model-gen steps reference <see cref="ComfyUIBackendExtension"/> params directly.
    /// In unit tests we don't initialize the full ComfyUIBackendExtension, so register the minimum param(s)
    /// needed to avoid null derefs while still validating workflow JSON structure.
    /// </summary>
    public static void EnsureComfySetClipDeviceRegistered()
    {
        if (ComfyUIBackendExtension.SetClipDevice is not null)
        {
            return;
        }

        ComfyUIBackendExtension.SetClipDevice = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Set CLIP Device (UnitTest Stub)",
            Description: "Stub param registered only for unit tests.",
            Default: "cpu",
            FeatureFlag: "set_clip_device",
            Group: T2IParamTypes.GroupAdvancedModelAddons,
            IsAdvanced: true,
            Toggleable: true,
            GetValues: (_) => ["cpu"]
        ));
    }

    /// <summary>
    /// Base2Edit stage0 inheritance reads <see cref="ComfyUIBackendExtension.SamplerParam"/> and friends directly.
    /// Unit tests do not initialize the full ComfyUIBackendExtension, so stub-register these params as needed.
    /// </summary>
    public static void EnsureComfySamplerSchedulerRegistered()
    {
        if (ComfyUIBackendExtension.SamplerParam is not null
            && ComfyUIBackendExtension.SchedulerParam is not null
            && ComfyUIBackendExtension.RefinerSamplerParam is not null
            && ComfyUIBackendExtension.RefinerSchedulerParam is not null)
        {
            return;
        }

        ComfyUIBackendExtension.SamplerParam ??= T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Sampler (UnitTest Stub)",
            Description: "Stub param registered only for unit tests.",
            Default: "euler",
            FeatureFlag: "comfyui",
            Group: T2IParamTypes.GroupSampling,
            Toggleable: true,
            GetValues: (_) => ["euler", "dpmpp_2m"]
        ));

        ComfyUIBackendExtension.SchedulerParam ??= T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Scheduler (UnitTest Stub)",
            Description: "Stub param registered only for unit tests.",
            Default: "normal",
            FeatureFlag: "comfyui",
            Group: T2IParamTypes.GroupSampling,
            Toggleable: true,
            GetValues: (_) => ["normal", "karras"]
        ));

        ComfyUIBackendExtension.RefinerSamplerParam ??= T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Refiner Sampler (UnitTest Stub)",
            Description: "Stub param registered only for unit tests.",
            Default: "euler",
            FeatureFlag: "comfyui",
            Group: T2IParamTypes.GroupSampling,
            Toggleable: true,
            GetValues: (_) => ["euler", "dpmpp_2m"]
        ));

        ComfyUIBackendExtension.RefinerSchedulerParam ??= T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Refiner Scheduler (UnitTest Stub)",
            Description: "Stub param registered only for unit tests.",
            Default: "normal",
            FeatureFlag: "comfyui",
            Group: T2IParamTypes.GroupSampling,
            Toggleable: true,
            GetValues: (_) => ["normal", "karras"]
        ));
    }
}

/// <summary>
/// Helper to snapshot/restore SwarmUI global/static state that unit tests often override.
/// Prefer this over repeated try/finally blocks in each test.
/// </summary>
internal sealed class SwarmUiTestContext : IDisposable
{
    private readonly Dictionary<string, T2IModelHandler> _priorModelSets;
    private readonly bool _priorIncludeHash;
    private readonly List<WorkflowGenerator.WorkflowGenStep> _priorModelGenSteps;
    private readonly ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>> _priorExtraModelProviders;

    public SwarmUiTestContext(
        bool disableImageMetadataModelHash = true,
        bool resetExtraModelProviders = true,
        bool clearModelGenSteps = true
    )
    {
        _priorModelSets = Program.T2IModelSets;
        _priorIncludeHash = Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash;
        _priorModelGenSteps = [.. WorkflowGenerator.ModelGenSteps];
        _priorExtraModelProviders = ModelsAPI.ExtraModelProviders;

        if (disableImageMetadataModelHash)
        {
            Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = false;
        }

        if (resetExtraModelProviders)
        {
            ModelsAPI.ExtraModelProviders = new ConcurrentDictionary<string, Func<string, Dictionary<string, JObject>>>(
                [
                    new KeyValuePair<string, Func<string, Dictionary<string, JObject>>>("unit_test", _ => new Dictionary<string, JObject>())
                ]);
        }

        if (clearModelGenSteps)
        {
            WorkflowGenerator.ModelGenSteps = [];
        }
    }

    public void Dispose()
    {
        WorkflowGenerator.ModelGenSteps = _priorModelGenSteps;
        ModelsAPI.ExtraModelProviders = _priorExtraModelProviders;
        Program.T2IModelSets = _priorModelSets;
        Program.ServerSettings.Metadata.ImageMetadataIncludeModelHash = _priorIncludeHash;
    }
}
