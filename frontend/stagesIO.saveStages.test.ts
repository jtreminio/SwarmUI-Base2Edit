import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { addInput, makeStage } from "./__test_helpers__";
import { saveStages } from "./stagesIO";

let triggerChangeSpy: jest.Spied<typeof globalThis.triggerChangeFor>;

beforeEach(() => {
    triggerChangeSpy = jest
        .spyOn(
            globalThis as unknown as {
                triggerChangeFor: typeof globalThis.triggerChangeFor;
            },
            "triggerChangeFor",
        )
        .mockImplementation(() => {});
    addInput("input_editstages", "");
});

afterEach(() => {
    triggerChangeSpy.mockRestore();
});

describe("saveStages", () => {
    it("updates input.value, calls triggerChangeFor, and calls onAfterSave when enabled", () => {
        const getIsEnabled = jest.fn(() => true) as unknown as () => boolean;
        const onAfterSave = jest.fn();
        const stage = makeStage();

        saveStages([stage], { getIsEnabled, onAfterSave });

        expect(
            (document.getElementById("input_editstages") as HTMLInputElement)
                ?.value,
        ).toBe(JSON.stringify([stage]));
        expect(triggerChangeSpy).toHaveBeenCalledTimes(1);
        expect(onAfterSave).toHaveBeenCalledTimes(1);
        expect(onAfterSave).toHaveBeenCalledWith(JSON.stringify([stage]));
    });

    it("skips triggerChangeFor but still calls onAfterSave when disabled", () => {
        const getIsEnabled = jest.fn(() => false) as unknown as () => boolean;
        const onAfterSave = jest.fn();
        const stage = makeStage();

        saveStages([stage], { getIsEnabled, onAfterSave });

        expect(
            (document.getElementById("input_editstages") as HTMLInputElement)
                ?.value,
        ).toBe(JSON.stringify([stage]));
        expect(triggerChangeSpy).not.toHaveBeenCalled();
        expect(onAfterSave).toHaveBeenCalledTimes(1);
        expect(onAfterSave).toHaveBeenCalledWith(JSON.stringify([stage]));
    });

    it("serializes empty array and calls onAfterSave with '[]'", () => {
        const getIsEnabled = jest.fn(() => true) as unknown as () => boolean;
        const onAfterSave = jest.fn();

        saveStages([], { getIsEnabled, onAfterSave });

        expect(
            (document.getElementById("input_editstages") as HTMLInputElement)
                ?.value,
        ).toBe("[]");
        expect(onAfterSave).toHaveBeenCalledTimes(1);
        expect(onAfterSave).toHaveBeenCalledWith("[]");
    });

    it("round-trips multiple stages through JSON.stringify", () => {
        const getIsEnabled = jest.fn(() => false) as unknown as () => boolean;
        const onAfterSave = jest.fn();
        const stages = [makeStage({ steps: 5 }), makeStage({ model: "other" })];

        saveStages(stages, { getIsEnabled, onAfterSave });

        expect(onAfterSave).toHaveBeenCalledWith(JSON.stringify(stages));
        const inputElement = document.getElementById(
            "input_editstages",
        ) as HTMLInputElement;
        expect(JSON.parse(inputElement.value)).toEqual(stages);
    });

    it("onAfterSave receives the serialized string, not the array reference", () => {
        const getIsEnabled = jest.fn(() => false) as unknown as () => boolean;
        const onAfterSave = jest.fn();
        const stage = makeStage();

        saveStages([stage], { getIsEnabled, onAfterSave });

        expect(typeof onAfterSave.mock.calls[0][0]).toBe("string");
        expect(onAfterSave.mock.calls[0][0]).toBe(JSON.stringify([stage]));
    });
});
