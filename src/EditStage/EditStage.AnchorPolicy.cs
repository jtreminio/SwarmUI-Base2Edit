using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public partial class EditStage
{
    /// <summary>
    /// Policy gate for final-step anchor behavior. Segment-after-refiner workflows should anchor
    /// from the current image tail so Base2Edit runs after those segment stages.
    /// </summary>
    private static bool ShouldPreferCurrentImageAnchor(SwarmUI.Builtin_ComfyUIBackend.WorkflowGenerator g, StageHook hook)
    {
        if (g?.UserInput is null || hook != StageHook.Refiner)
        {
            return false;
        }

        string segmentApplyAfter = g.UserInput.Get(T2IParamTypes.SegmentApplyAfter, "Refiner");
        if (!string.Equals(segmentApplyAfter, "Refiner", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        PromptRegion prompt = new(g.UserInput.Get(T2IParamTypes.Prompt, ""));
        return prompt.Parts.Any(p => p.Type == PromptRegion.PartType.Segment);
    }
}
