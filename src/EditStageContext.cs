using SwarmUI.Builtin_ComfyUIBackend;

namespace Base2Edit;

/// <summary>
/// Per-stage source-of-truth for an edit stage. Aggregates the spec, prompt section id, and
/// per-stage state (model, params, source media/vae) that the stage runner currently passes
/// around as loose method args. Mirrors VideoStages's StageFrame.
///
/// Fields populated incrementally as the runner discovers state: ModelState after
/// PrepareModelAndVae, Parameters after upscale + steps/CFG resolution, Conditioning after
/// the prompt is encoded. SourceMedia/SourceVae are snapshotted at runner entry.
/// </summary>
internal sealed class EditStageContext
{
    public EditStageContext(
        StageSpec stage,
        int sectionId,
        WGNodeData sourceMedia,
        WGNodeData sourceVae)
    {
        Stage = stage;
        SectionId = sectionId;
        SourceMedia = sourceMedia;
        SourceVae = sourceVae;
    }

    public StageSpec Stage { get; }
    public int SectionId { get; }
    public WGNodeData SourceMedia { get; }
    public WGNodeData SourceVae { get; }

    public ModelState ModelState { get; set; }
    public Parameters Parameters { get; set; }
    public Conditioning Conditioning { get; set; }
    public bool ReencodedFromCurrentImage { get; set; }
}
