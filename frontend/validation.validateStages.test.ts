import { describe, expect, it } from "@jest/globals";
import { makeStage } from "./__test_helpers__";
import { validateStages } from "./validation";

describe("validateStages", () => {
    it("returns empty array for empty input", () => {
        expect(validateStages([])).toEqual([]);
    });

    it("returns no errors when applyAfter is Refiner", () => {
        expect(validateStages([makeStage({ applyAfter: "Refiner" })])).toEqual(
            [],
        );
    });

    it("returns no errors when stage 2 references Edit Stage 1 (earlier stage)", () => {
        expect(
            validateStages([
                makeStage(),
                makeStage({ applyAfter: "Edit Stage 1" }),
            ]),
        ).toEqual([]);
    });

    it("returns error when stage 1 applyAfter is Edit Stage 1 (same stage)", () => {
        const result = validateStages([
            makeStage({ applyAfter: "Edit Stage 1" }),
        ]);
        expect(result).toHaveLength(1);
        expect(result[0]).toBe(
            `Base2Edit: Edit Stage 1 cannot Apply After "Edit Stage 1" (must reference an earlier stage).`,
        );
    });

    it("returns error when stage 2 applyAfter is Edit Stage 3 (future stage)", () => {
        const result = validateStages([
            makeStage(),
            makeStage({ applyAfter: "Edit Stage 3" }),
        ]);
        expect(result).toHaveLength(1);
        expect(result[0]).toBe(
            `Base2Edit: Edit Stage 2 cannot Apply After "Edit Stage 3" (must reference an earlier stage).`,
        );
    });

    it("returns error when stage 2 applyAfter is Edit Stage 2 (same stage)", () => {
        const result = validateStages([
            makeStage(),
            makeStage({ applyAfter: "Edit Stage 2" }),
        ]);
        expect(result).toHaveLength(1);
        expect(result[0]).toBe(
            `Base2Edit: Edit Stage 2 cannot Apply After "Edit Stage 2" (must reference an earlier stage).`,
        );
    });

    it("returns only errors for invalid stages in a mixed array", () => {
        const result = validateStages([
            makeStage({ applyAfter: "Refiner" }),
            makeStage({ applyAfter: "Edit Stage 1" }),
            makeStage({ applyAfter: "Edit Stage 5" }),
        ]);
        expect(result).toHaveLength(1);
        expect(result[0]).toBe(
            `Base2Edit: Edit Stage 3 cannot Apply After "Edit Stage 5" (must reference an earlier stage).`,
        );
    });

    it("ignores applyAfter values that do not match Edit Stage pattern", () => {
        expect(
            validateStages([
                makeStage({ applyAfter: "some random text" }),
                makeStage({ applyAfter: "EditStage1" }),
            ]),
        ).toEqual([]);
    });
});
