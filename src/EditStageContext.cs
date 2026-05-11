using SwarmUI.Builtin_ComfyUIBackend;

namespace Base2Edit;

/// <summary>
/// Per-stage source-of-truth for an edit stage. Aggregates the spec, prompt section id, and
/// per-stage state (model, params, source media/vae) that the stage runner currently passes
/// around as loose method args. Mirrors VideoStages's StageFrame.
///
/// Skeleton only: not yet threaded through EditStage / StageRunner. Migration of those is
/// a follow-up round.
/// </summary>
internal sealed class EditStageContext
{
    public EditStageContext(
        StageSpec stage,
        int sectionId,
        ModelState modelState,
        Parameters parameters,
        WGNodeData sourceMedia,
        WGNodeData sourceVae)
    {
        Stage = stage;
        SectionId = sectionId;
        ModelState = modelState;
        Parameters = parameters;
        SourceMedia = sourceMedia;
        SourceVae = sourceVae;
    }

    public StageSpec Stage { get; }
    public int SectionId { get; }
    public ModelState ModelState { get; }
    public Parameters Parameters { get; }
    public WGNodeData SourceMedia { get; }
    public WGNodeData SourceVae { get; }

    public Conditioning Conditioning { get; set; }
    public bool ReencodedFromCurrentImage { get; set; }
}
