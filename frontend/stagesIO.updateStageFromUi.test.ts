import { describe, expect, it } from "@jest/globals";
import {
    addCheckbox,
    addInput,
    addToggle,
    makeStage,
} from "./__test_helpers__";
import { updateStageFromUi } from "./stagesIO";

describe("updateStageFromUi", () => {
    describe("all toggles enabled", () => {
        it("updates every field when all toggles are checked and inputs are populated", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addCheckbox("base2edit_stage_1_keeppreeditimage", true);
            addCheckbox("base2edit_stage_1_editrefineonly", true);
            addInput("base2edit_stage_1_applyafter", "Hires");
            addInput("base2edit_stage_1_editcontrol", "0.8");
            addInput("base2edit_stage_1_editupscale", "2");
            addInput("base2edit_stage_1_editupscalemethod", "nearest");
            addInput("base2edit_stage_1_editmodel", "my-model");
            addInput("base2edit_stage_1_editvae", "my-vae");
            addToggle("base2edit_stage_1_editvae", true);
            addInput("base2edit_stage_1_editsteps", "30");
            addInput("base2edit_stage_1_editcfgscale", "7.5");
            addToggle("base2edit_stage_1_editcfgscale", true);
            addInput("base2edit_stage_1_editsampler", "euler");
            addToggle("base2edit_stage_1_editsampler", true);
            addInput("base2edit_stage_1_editscheduler", "karras");
            addToggle("base2edit_stage_1_editscheduler", true);
            updateStageFromUi(prefix, stage);
            expect(stage.keepPreEditImage).toBe(true);
            expect(stage.refineOnly).toBe(true);
            expect(stage.applyAfter).toBe("Hires");
            expect(stage.control).toBe(0.8);
            expect(stage.upscale).toBe(2);
            expect(stage.upscaleMethod).toBe("nearest");
            expect(stage.model).toBe("my-model");
            expect(stage.vae).toBe("my-vae");
            expect(stage.steps).toBe(30);
            expect(stage.cfgScale).toBe(7.5);
            expect(stage.sampler).toBe("euler");
            expect(stage.scheduler).toBe("karras");
        });
    });

    describe("all toggles unchecked", () => {
        it("sets vae, cfgScale, sampler, scheduler to null when toggles are unchecked", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addInput("base2edit_stage_1_editvae", "my-vae");
            addToggle("base2edit_stage_1_editvae", false);
            addInput("base2edit_stage_1_editcfgscale", "7.5");
            addToggle("base2edit_stage_1_editcfgscale", false);
            addInput("base2edit_stage_1_editsampler", "euler");
            addToggle("base2edit_stage_1_editsampler", false);
            addInput("base2edit_stage_1_editscheduler", "karras");
            addToggle("base2edit_stage_1_editscheduler", false);
            updateStageFromUi(prefix, stage);
            expect(stage.vae).toBe(null);
            expect(stage.cfgScale).toBe(null);
            expect(stage.sampler).toBe(null);
            expect(stage.scheduler).toBe(null);
        });
    });

    describe("missing toggle element", () => {
        it("treats missing toggle as enabled and reads the input value", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addInput("base2edit_stage_1_editvae", "fallback-vae");
            updateStageFromUi(prefix, stage);
            expect(stage.vae).toBe("fallback-vae");
        });
    });

    describe("missing input element for non-nullable field", () => {
        it("falls back to existing stage value when input element is absent for model", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage({ model: "original-model" });
            updateStageFromUi(prefix, stage);
            expect(stage.model).toBe("original-model");
        });

        it("falls back to existing stage value when input element is absent for applyAfter", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage({ applyAfter: "Refiner" });
            updateStageFromUi(prefix, stage);
            expect(stage.applyAfter).toBe("Refiner");
        });

        it("falls back to existing stage value when input element is absent for upscaleMethod", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage({ upscaleMethod: "lanczos" });
            updateStageFromUi(prefix, stage);
            expect(stage.upscaleMethod).toBe("lanczos");
        });
    });

    describe("numeric coercion", () => {
        it("parses editcontrol string to float", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addInput("base2edit_stage_1_editcontrol", "0.5");
            updateStageFromUi(prefix, stage);
            expect(stage.control).toBe(0.5);
            expect(typeof stage.control).toBe("number");
        });

        it("parses editsteps string to int", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addInput("base2edit_stage_1_editsteps", "20");
            updateStageFromUi(prefix, stage);
            expect(stage.steps).toBe(20);
            expect(Number.isInteger(stage.steps)).toBe(true);
        });

        it("parses editupscale string to float", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addInput("base2edit_stage_1_editupscale", "1.5");
            updateStageFromUi(prefix, stage);
            expect(stage.upscale).toBe(1.5);
        });
    });

    describe("boolean fields", () => {
        it("sets keepPreEditImage to true when checkbox is checked", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addCheckbox("base2edit_stage_1_keeppreeditimage", true);
            updateStageFromUi(prefix, stage);
            expect(stage.keepPreEditImage).toBe(true);
        });

        it("sets keepPreEditImage to false when checkbox is unchecked", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addCheckbox("base2edit_stage_1_keeppreeditimage", false);
            updateStageFromUi(prefix, stage);
            expect(stage.keepPreEditImage).toBe(false);
        });

        it("sets refineOnly to true when checkbox is checked", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addCheckbox("base2edit_stage_1_editrefineonly", true);
            updateStageFromUi(prefix, stage);
            expect(stage.refineOnly).toBe(true);
        });

        it("sets refineOnly to false when checkbox is unchecked", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            addCheckbox("base2edit_stage_1_editrefineonly", false);
            updateStageFromUi(prefix, stage);
            expect(stage.refineOnly).toBe(false);
        });

        it("coerces missing boolean element to false via double-negation", () => {
            const prefix = "base2edit_stage_1_";
            const stage = makeStage();
            updateStageFromUi(prefix, stage);
            expect(stage.keepPreEditImage).toBe(false);
        });
    });

    describe("QA gap fills", () => {
        describe("Gap 1: Fallback paths for non-nullable numerics when input element is ABSENT", () => {
            it("falls back to existing stage value when editcontrol input element is absent", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ control: 0.7 });
                updateStageFromUi(prefix, stage);
                expect(stage.control).toBe(0.7);
            });

            it("falls back to existing stage value when editupscale input element is absent", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ upscale: 2 });
                updateStageFromUi(prefix, stage);
                expect(stage.upscale).toBe(2);
            });

            it("falls back to existing stage value when editsteps input element is absent", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ steps: 25 });
                updateStageFromUi(prefix, stage);
                expect(stage.steps).toBe(25);
            });
        });

        describe("Gap 2: Nullable fields when toggle is ON but input element is ABSENT", () => {
            it("falls back to existing stage.vae when toggle is checked but input is absent", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ vae: "original-vae" });
                addToggle("base2edit_stage_1_editvae", true);
                updateStageFromUi(prefix, stage);
                expect(stage.vae).toBe("original-vae");
            });

            it("falls back to existing stage.cfgScale when toggle is checked but input is absent", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ cfgScale: 4.5 });
                addToggle("base2edit_stage_1_editcfgscale", true);
                updateStageFromUi(prefix, stage);
                expect(stage.cfgScale).toBe(4.5);
            });

            it("falls back to existing stage.sampler when toggle is checked but input is absent", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ sampler: "dpm" });
                addToggle("base2edit_stage_1_editsampler", true);
                updateStageFromUi(prefix, stage);
                expect(stage.sampler).toBe("dpm");
            });

            it("falls back to existing stage.scheduler when toggle is checked but input is absent", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ scheduler: "karras" });
                addToggle("base2edit_stage_1_editscheduler", true);
                updateStageFromUi(prefix, stage);
                expect(stage.scheduler).toBe("karras");
            });
        });

        describe("Gap 3: ?? vs || distinction (empty string behavior)", () => {
            it("editcontrol with empty-string input becomes NaN (?? does not fall back on empty string)", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ control: 0.9 });
                addInput("base2edit_stage_1_editcontrol", "");
                updateStageFromUi(prefix, stage);
                expect(Number.isNaN(stage.control)).toBe(true);
            });

            it("editsteps with empty-string input falls back to existing stage.steps (|| treats empty string as falsy)", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage({ steps: 17 });
                addInput("base2edit_stage_1_editsteps", "");
                updateStageFromUi(prefix, stage);
                expect(stage.steps).toBe(17);
            });
        });

        describe("Gap 4: parseInt/parseFloat on non-numeric strings", () => {
            it("editcontrol with non-numeric string becomes NaN", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage();
                addInput("base2edit_stage_1_editcontrol", "abc");
                updateStageFromUi(prefix, stage);
                expect(Number.isNaN(stage.control)).toBe(true);
            });

            it("editsteps with non-numeric string becomes NaN", () => {
                const prefix = "base2edit_stage_1_";
                const stage = makeStage();
                addInput("base2edit_stage_1_editsteps", "abc");
                updateStageFromUi(prefix, stage);
                expect(Number.isNaN(stage.steps)).toBe(true);
            });
        });
    });
});
