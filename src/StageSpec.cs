using SwarmUI.Text2Image;

namespace Base2Edit;

public enum ModelSource { Base, Refiner, Specific }

public enum ParentKind { Base, Refiner, Edit }

public sealed record StageSpec(
    int Id,
    ParentKind ParentKind,
    int ParentStageId,
    bool KeepPreEditImage,
    bool RefineOnly,
    double Control,
    T2IModel Model,
    ModelSource ModelSource,
    T2IModel Vae,
    double Upscale,
    string UpscaleMethod,
    int Steps,
    double CfgScale,
    string? Sampler,
    string? Scheduler,
    string PositivePrompt,
    string NegativePrompt,
    StageLoras Loras,
    long Seed,
    double Guidance,
    List<StageSpec> Children = default!
);
