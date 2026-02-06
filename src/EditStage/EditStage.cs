using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;
using Newtonsoft.Json.Linq;

namespace Base2Edit;

internal record EditPrompts(string Positive, string Negative);

internal record EditModelState(
    JArray Model,
    JArray Clip,
    JArray Vae,
    JArray PreEditVae,
    bool MustReencode
);

internal record EditParameters(
    int Width,
    int Height,
    int Steps,
    double Cfg,
    double Control,
    double Guidance,
    long Seed,
    string Sampler,
    string Scheduler
);

internal record EditConditioning(JArray Positive, JArray Negative);

public partial class EditStage
{
    private const int PreEditImageSaveId = 50200;
    private const int ParallelEditSaveId = 50300;
    private const int EditSeedOffset = 2;

    public static void Run(WorkflowGenerator g, bool isFinalStep)
    {
        // ACTIVE contract:
        // - Extension is considered ACTIVE when the root-level (stage0) "Edit Model" param is present.
        // - When ACTIVE, stage0 is ALWAYS included. Additional stages are optional and come from JSON.
        // - There is no scenario where stage1+ arrives without stage0 (stage0 comes from root-level fields).
        if (g?.UserInput is null || !IsExtensionActive(g.UserInput))
        {
            return;
        }

        if (!isFinalStep)
        {
            CaptureBaseStageModelState(g);
        }

        _ = TryGetEditStages(g, out List<JsonStageSpec> jsonStages);
        List<StageSpec> stages = BuildUnifiedStages(g, jsonStages);
        RunStages(g, stages, isFinalStep);
    }

    private static bool IsExtensionActive(T2IParamInput input)
    {
        T2IParamType type = Base2EditExtension.EditModel?.Type;
        return type is not null && input.TryGetRaw(type, out _);
    }
}
