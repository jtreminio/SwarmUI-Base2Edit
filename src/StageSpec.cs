namespace Base2Edit;

/// <summary>
/// Resolved per-stage configuration for an edit stage. Defaults from base / refiner / parent
/// stage have already been applied; every field is concrete (no nullable inheritance markers).
/// Produced by <see cref="Base2EditSpecParser"/>.
/// </summary>
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
    List<StageSpec> Children = default!
);
