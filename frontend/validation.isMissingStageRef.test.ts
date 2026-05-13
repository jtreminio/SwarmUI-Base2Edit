import { describe, expect, it } from "@jest/globals";
import { isMissingStageRef } from "./validation";

describe("isMissingStageRef", () => {
    it("returns false for a non-matching string like 'Refiner'", () => {
        expect(isMissingStageRef("Refiner", [1, 2, 3])).toBe(false);
    });

    it("returns false for an empty string", () => {
        expect(isMissingStageRef("", [1, 2, 3])).toBe(false);
    });

    it("returns false when stage id is present in stageIds", () => {
        expect(isMissingStageRef("Edit Stage 2", [1, 2, 3])).toBe(false);
    });

    it("returns true when stage id is absent from stageIds", () => {
        expect(isMissingStageRef("Edit Stage 5", [1, 2, 3])).toBe(true);
    });

    it("returns true when stageIds is empty", () => {
        expect(isMissingStageRef("Edit Stage 0", [])).toBe(true);
    });

    it("returns false for lowercase variant (case-sensitive regex)", () => {
        expect(isMissingStageRef("edit stage 2", [1, 2, 3])).toBe(false);
    });

    it("returns false when digit is missing after 'Edit Stage '", () => {
        expect(isMissingStageRef("Edit Stage ", [1, 2, 3])).toBe(false);
    });

    it("returns false when digit has a non-digit suffix (anchored regex)", () => {
        expect(isMissingStageRef("Edit Stage 2x", [1, 2, 3])).toBe(false);
    });

    it("returns false when string has a leading space (anchored regex)", () => {
        expect(isMissingStageRef(" Edit Stage 2", [1, 2, 3])).toBe(false);
    });

    it("returns false for a different prefix like 'Stage 2'", () => {
        expect(isMissingStageRef("Stage 2", [1, 2, 3])).toBe(false);
    });

    it("returns false when multi-digit stage id is present in stageIds", () => {
        expect(isMissingStageRef("Edit Stage 10", [1, 2, 10])).toBe(false);
    });

    it("returns true when multi-digit stage id is absent from stageIds", () => {
        expect(isMissingStageRef("Edit Stage 10", [1, 2, 3])).toBe(true);
    });

    it("returns true for triple-digit stage id when stageIds is empty", () => {
        expect(isMissingStageRef("Edit Stage 100", [])).toBe(true);
    });
});
