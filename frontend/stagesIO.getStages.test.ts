import { describe, expect, it } from "@jest/globals";
import { addInput, makeStage } from "./__test_helpers__";
import { getStages } from "./stagesIO";
import type { Stage } from "./types";

describe("getStages", () => {
    it("returns parsed Stage array when input contains valid JSON", () => {
        addInput("input_editstages", JSON.stringify([makeStage()]));
        expect(getStages()).toEqual([makeStage()]);
    });

    it("returns [] when input value is empty string (catch path, not ?? fallback)", () => {
        addInput("input_editstages", "");
        expect(getStages()).toEqual([]);
    });

    it("returns [] when input_editstages element is absent from DOM", () => {
        expect(getStages()).toEqual([]);
    });

    it("returns [] for malformed JSON '{'", () => {
        addInput("input_editstages", "{");
        expect(getStages()).toEqual([]);
    });

    it("returns [] for malformed JSON 'not json'", () => {
        addInput("input_editstages", "not json");
        expect(getStages()).toEqual([]);
    });

    it("returns parsed non-array value without type validation (function does not guard against non-array JSON)", () => {
        addInput("input_editstages", "42");
        expect(getStages()).toBe(42);
    });

    it("round-trip: stages written as JSON.stringify are returned with deep equality", () => {
        const stages: Stage[] = [
            makeStage({ steps: 5 }),
            makeStage({ model: "other" }),
        ];
        addInput("input_editstages", JSON.stringify(stages));
        expect(getStages()).toEqual(stages);
    });
});
