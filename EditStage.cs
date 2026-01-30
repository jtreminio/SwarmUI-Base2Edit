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

        string prompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        // ACTIVE prompt contract:
        // - "<edit>" (global) applies to all edit stages
        // - "<edit[0]>" applies to stage0 only (and must exist to activate stage0 if <edit> is absent)
        // - "<edit[n]>" for n>0 does NOT activate the extension by itself
        if (string.IsNullOrWhiteSpace(prompt) || !HasStage0OrGlobalEditTag(prompt))
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

    private static bool HasStage0OrGlobalEditTag(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Accept either:
        // - raw user syntax: <edit>, <edit:...>, <edit[0]>, <edit[0]:...>
        // - processed syntax: <edit//cid=X>
        // where X corresponds to the global section ID or stage0's section ID.
        int globalCid = Base2EditExtension.SectionID_Edit;
        int stage0Cid = Base2EditExtension.EditSectionIdForStage(0);

        foreach (string piece in prompt.Split('<'))
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                continue;
            }

            string tag = piece[..end];
            string prefix = tag;
            int colon = tag.IndexOf(':');
            if (colon != -1)
            {
                prefix = tag[..colon];
            }
            prefix = prefix.Split('/')[0];

            // Processed syntax: <edit//cid=X>
            int cidCut = tag.LastIndexOf("//cid=", StringComparison.OrdinalIgnoreCase);
            if (cidCut != -1
                && string.Equals(prefix, "edit", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(tag[(cidCut + "//cid=".Length)..], out int cid))
            {
                if (cid == globalCid || cid == stage0Cid)
                {
                    return true;
                }
                continue;
            }

            // Raw syntax: <edit> or <edit[0]>
            string preData = null;
            if (prefix.EndsWith(']') && prefix.Contains('['))
            {
                int open = prefix.LastIndexOf('[');
                if (open != -1)
                {
                    preData = prefix[(open + 1)..^1];
                    prefix = prefix[..open];
                }
            }

            if (!string.Equals(prefix, "edit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (preData is null)
            {
                return true; // <edit> (global)
            }

            if (int.TryParse(preData, out int stageIndex) && stageIndex == 0)
            {
                return true; // <edit[0]>
            }
        }

        return false;
    }
}
