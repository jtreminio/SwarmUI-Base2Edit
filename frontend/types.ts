export interface RootStage {
    refineOnly: HTMLInputElement;
    applyAfter?: string;
    control: HTMLInputElement;
    upscale: HTMLInputElement;
    upscaleMethod: HTMLSelectElement;
    model: HTMLSelectElement;
    vae: HTMLSelectElement;
    steps: HTMLInputElement;
    cfgScale: HTMLInputElement;
    sampler: HTMLSelectElement;
    scheduler: HTMLSelectElement;
}

export interface Stage {
    keepPreEditImage: boolean;
    refineOnly?: boolean;
    expanded?: boolean;
    skipped?: boolean;
    applyAfter: string;
    control: number;
    upscale: number;
    upscaleMethod: string;
    model: string;
    vae: string | null;
    steps: number;
    cfgScale: number | null;
    sampler: string | null;
    scheduler: string | null;
}
