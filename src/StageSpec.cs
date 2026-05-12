namespace Base2Edit;

public sealed record StageSpec(
    int Id,
    string ApplyAfter,
    bool KeepPreEditImage,
    bool RefineOnly,
    double Control,
    string Model,
    string Vae,
    double Upscale,
    string UpscaleMethod,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    bool HasVaeOverride,
    string PositivePrompt,
    string NegativePrompt,
    StageLoras Loras,
    long Seed,
    double Guidance,
    List<StageSpec> Children = default!
);
