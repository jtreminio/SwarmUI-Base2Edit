import { beforeEach, describe, expect, it } from "@jest/globals";
import { addCheckbox, addInput, addToggle } from "./__test_helpers__";
import { createStage } from "./rootStage";

beforeEach(() => {
    addCheckbox("input_keeppreeditimage");
    addCheckbox("input_refineonly");
    addInput("input_editcontrol", "0.5");
    addInput("input_editupscale", "1");
    addInput("input_editupscalemethod", "lanczos");
    addInput("input_editmodel", "test-model");
    addInput("input_editsteps", "20");
});

describe("readToggleableRoot (via createStage)", () => {
    describe("vae field", () => {
        it("returns null when input element is missing", () => {
            const result = createStage("Refiner");
            expect(result.vae).toBeNull();
        });

        it("returns the value when element is present and no toggle exists", () => {
            addInput("input_editvae", "my-vae");
            const result = createStage("Refiner");
            expect(result.vae).toBe("my-vae");
        });

        it("returns the value when toggle is present and checked", () => {
            addInput("input_editvae", "my-vae");
            addToggle("input_editvae", true);
            const result = createStage("Refiner");
            expect(result.vae).toBe("my-vae");
        });

        it("returns null when toggle is present and unchecked", () => {
            addInput("input_editvae", "my-vae");
            addToggle("input_editvae", false);
            const result = createStage("Refiner");
            expect(result.vae).toBeNull();
        });
    });

    describe("cfgScale field — readToggleableRoot via parseFloat", () => {
        it('returns NaN when input element is missing (readToggleableRoot returns null, ?? "" → parseFloat("") = NaN)', () => {
            const result = createStage("Refiner");
            expect(Number.isNaN(result.cfgScale as number)).toBe(true);
        });

        it("returns numeric value when element is present and no toggle", () => {
            addInput("input_editcfgscale", "7.5");
            const result = createStage("Refiner");
            expect(result.cfgScale).toBe(7.5);
        });
    });
});
