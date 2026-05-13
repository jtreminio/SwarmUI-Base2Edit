import { describe, expect, it } from "@jest/globals";
import { buildApplyAfterList } from "./validation";

describe("buildApplyAfterList", () => {
    it("returns Refiner plus filtered+sorted stage ids less than stageId", () => {
        expect(buildApplyAfterList([1, 2, 3], 3, "")).toEqual([
            "Refiner",
            "Edit Stage 1",
            "Edit Stage 2",
        ]);
    });

    it("returns only Refiner when no stageIds are less than stageId", () => {
        expect(buildApplyAfterList([1, 2, 3], 1, "")).toEqual(["Refiner"]);
    });

    it("does not duplicate currentVal when it is already in the list", () => {
        expect(buildApplyAfterList([1, 2, 3], 3, "Edit Stage 1")).toEqual([
            "Refiner",
            "Edit Stage 1",
            "Edit Stage 2",
        ]);
    });

    it("prepends stale currentVal at index 0 when not in list", () => {
        expect(buildApplyAfterList([1, 2, 3], 3, "Edit Stage 99")).toEqual([
            "Edit Stage 99",
            "Refiner",
            "Edit Stage 1",
            "Edit Stage 2",
        ]);
    });

    it("sorts unsorted stageIds ascending in output", () => {
        expect(buildApplyAfterList([3, 1, 2], 4, "")).toEqual([
            "Refiner",
            "Edit Stage 1",
            "Edit Stage 2",
            "Edit Stage 3",
        ]);
    });

    it("does not prepend empty string currentVal", () => {
        expect(buildApplyAfterList([1, 2, 3], 3, "")).toEqual([
            "Refiner",
            "Edit Stage 1",
            "Edit Stage 2",
        ]);
    });

    it("does not duplicate Refiner when currentVal is Refiner", () => {
        expect(buildApplyAfterList([1, 2, 3], 3, "Refiner")).toEqual([
            "Refiner",
            "Edit Stage 1",
            "Edit Stage 2",
        ]);
    });

    it("excludes stageId itself and any ids greater than stageId (strict less-than)", () => {
        expect(buildApplyAfterList([1, 2, 3], 2, "")).toEqual([
            "Refiner",
            "Edit Stage 1",
        ]);
    });

    it("returns only Refiner when stageIds is empty", () => {
        expect(buildApplyAfterList([], 1, "")).toEqual(["Refiner"]);
    });
});
