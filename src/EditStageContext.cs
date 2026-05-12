using SwarmUI.Builtin_ComfyUIBackend;

namespace Base2Edit;

internal sealed class EditStageContext(
    StageSpec stage,
    int sectionId,
    WGNodeData sourceMedia,
    WGNodeData sourceVae)
{
    public StageSpec Stage { get; } = stage;
    public int SectionId { get; } = sectionId;
    public WGNodeData SourceMedia { get; } = sourceMedia;
    public WGNodeData SourceVae { get; } = sourceVae;

    public ModelState ModelState { get; set; }
    public Parameters Parameters { get; set; }
    public Conditioning Conditioning { get; set; }
    public bool ReencodedFromCurrentImage { get; set; }
}
