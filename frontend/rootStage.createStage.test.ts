import { describe, expect, it } from "@jest/globals";
import { addCheckbox, addInput, addToggle } from "./__test_helpers__";
import { createStage } from "./rootStage";

const setupBaseline = (): void => {
    addCheckbox("input_keeppreeditimage", false);
    addCheckbox("input_refineonly", false);
    addInput("input_editcontrol", "0.5");
    addInput("input_editupscale", "1");
    addInput("input_editupscalemethod", "lanczos");
    addInput("input_editmodel", "test-model");
    addInput("input_editsteps", "20");
};

describe("cfgScale numeric coercion", () => {
    it("toggle off → cfgScale is NaN", () => {
        setupBaseline();
        addInput("input_editcfgscale", "7.5");
        addToggle("input_editcfgscale", false);

        const result = createStage("Refiner");

        expect(Number.isNaN(result.cfgScale as number)).toBe(true);
    });

    it("toggle on, valid float '7.5' → cfgScale === 7.5", () => {
        setupBaseline();
        addInput("input_editcfgscale", "7.5");
        addToggle("input_editcfgscale", true);

        const result = createStage("Refiner");

        expect(result.cfgScale).toBe(7.5);
    });

    it("toggle on, empty string value '' → NaN", () => {
        setupBaseline();
        addInput("input_editcfgscale", "");
        addToggle("input_editcfgscale", true);

        const result = createStage("Refiner");

        expect(Number.isNaN(result.cfgScale)).toBe(true);
    });

    it("no toggle element present → reads value directly", () => {
        setupBaseline();
        addInput("input_editcfgscale", "3.14");

        const result = createStage("Refiner");

        expect(result.cfgScale).toBe(3.14);
    });
});

describe("steps numeric coercion", () => {
    it("valid int string '20' → steps === 20", () => {
        setupBaseline();

        const result = createStage("Refiner");

        expect(result.steps).toBe(20);
    });

    it("float string '3.7' → truncates to 3 via parseInt", () => {
        addCheckbox("input_keeppreeditimage", false);
        addCheckbox("input_refineonly", false);
        addInput("input_editcontrol", "0.5");
        addInput("input_editupscale", "1");
        addInput("input_editupscalemethod", "lanczos");
        addInput("input_editmodel", "test-model");
        addInput("input_editsteps", "3.7");

        const result = createStage("Refiner");

        expect(result.steps).toBe(3);
    });

    it("empty string '' → NaN", () => {
        addCheckbox("input_keeppreeditimage", false);
        addCheckbox("input_refineonly", false);
        addInput("input_editcontrol", "0.5");
        addInput("input_editupscale", "1");
        addInput("input_editupscalemethod", "lanczos");
        addInput("input_editmodel", "test-model");
        addInput("input_editsteps", "");

        const result = createStage("Refiner");

        expect(Number.isNaN(result.steps)).toBe(true);
    });

    it("non-numeric string 'abc' → NaN", () => {
        addCheckbox("input_keeppreeditimage", false);
        addCheckbox("input_refineonly", false);
        addInput("input_editcontrol", "0.5");
        addInput("input_editupscale", "1");
        addInput("input_editupscalemethod", "lanczos");
        addInput("input_editmodel", "test-model");
        addInput("input_editsteps", "abc");

        const result = createStage("Refiner");

        expect(Number.isNaN(result.steps)).toBe(true);
    });
});

describe("control and upscale parseFloat", () => {
    it("control parses float from input value", () => {
        addCheckbox("input_keeppreeditimage", false);
        addCheckbox("input_refineonly", false);
        addInput("input_editcontrol", "0.75");
        addInput("input_editupscale", "1");
        addInput("input_editupscalemethod", "lanczos");
        addInput("input_editmodel", "test-model");
        addInput("input_editsteps", "20");

        const result = createStage("Refiner");

        expect(result.control).toBe(0.75);
    });

    it("upscale parses float from input value", () => {
        addCheckbox("input_keeppreeditimage", false);
        addCheckbox("input_refineonly", false);
        addInput("input_editcontrol", "0.5");
        addInput("input_editupscale", "2.0");
        addInput("input_editupscalemethod", "lanczos");
        addInput("input_editmodel", "test-model");
        addInput("input_editsteps", "20");

        const result = createStage("Refiner");

        expect(result.upscale).toBe(2);
    });
});

describe("toggle gating — vae, sampler, scheduler", () => {
    it("vae: toggle off → null", () => {
        setupBaseline();
        addInput("input_editvae", "my-vae");
        addToggle("input_editvae", false);

        const result = createStage("Refiner");

        expect(result.vae).toBe(null);
    });

    it("vae: toggle on → string value", () => {
        setupBaseline();
        addInput("input_editvae", "my-vae");
        addToggle("input_editvae", true);

        const result = createStage("Refiner");

        expect(result.vae).toBe("my-vae");
    });

    it("sampler: toggle off → null", () => {
        setupBaseline();
        addInput("input_editsampler", "euler");
        addToggle("input_editsampler", false);

        const result = createStage("Refiner");

        expect(result.sampler).toBe(null);
    });

    it("sampler: toggle on → string value", () => {
        setupBaseline();
        addInput("input_editsampler", "euler");
        addToggle("input_editsampler", true);

        const result = createStage("Refiner");

        expect(result.sampler).toBe("euler");
    });

    it("scheduler: toggle off → null", () => {
        setupBaseline();
        addInput("input_editscheduler", "karras");
        addToggle("input_editscheduler", false);

        const result = createStage("Refiner");

        expect(result.scheduler).toBe(null);
    });

    it("scheduler: toggle on → string value", () => {
        setupBaseline();
        addInput("input_editscheduler", "karras");
        addToggle("input_editscheduler", true);

        const result = createStage("Refiner");

        expect(result.scheduler).toBe("karras");
    });
});

describe("boolean fields", () => {
    it("keepPreEditImage reflects .checked = true", () => {
        addCheckbox("input_keeppreeditimage", true);
        addCheckbox("input_refineonly", false);
        addInput("input_editcontrol", "0.5");
        addInput("input_editupscale", "1");
        addInput("input_editupscalemethod", "lanczos");
        addInput("input_editmodel", "test-model");
        addInput("input_editsteps", "20");

        const result = createStage("Refiner");

        expect(result.keepPreEditImage).toBe(true);
    });

    it("keepPreEditImage reflects .checked = false", () => {
        setupBaseline();

        const result = createStage("Refiner");

        expect(result.keepPreEditImage).toBe(false);
    });

    it("refineOnly reflects .checked = true", () => {
        addCheckbox("input_keeppreeditimage", false);
        addCheckbox("input_refineonly", true);
        addInput("input_editcontrol", "0.5");
        addInput("input_editupscale", "1");
        addInput("input_editupscalemethod", "lanczos");
        addInput("input_editmodel", "test-model");
        addInput("input_editsteps", "20");

        const result = createStage("Refiner");

        expect(result.refineOnly).toBe(true);
    });
});

describe("applyAfter passthrough", () => {
    it("applyAfter is preserved as-is in result", () => {
        setupBaseline();

        const result = createStage("MyRefiner");

        expect(result.applyAfter).toBe("MyRefiner");
    });
});
