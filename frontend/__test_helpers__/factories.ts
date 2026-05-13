import type { Stage } from "../types";

export const makeStage = (overrides: Partial<Stage> = {}): Stage => {
    return {
        keepPreEditImage: false,
        refineOnly: false,
        applyAfter: "Refiner",
        control: 0.5,
        upscale: 1,
        upscaleMethod: "lanczos",
        model: "test-model",
        vae: null,
        steps: 20,
        cfgScale: null,
        sampler: null,
        scheduler: null,
        ...overrides,
    };
};
