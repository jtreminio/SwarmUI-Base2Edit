import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { addSelect, makeStage } from "./__test_helpers__";
import { createObservers } from "./observers";
import type { Stage } from "./types";
import { cleanApplyAfterOptions, validateApplyAfter } from "./validation";

jest.mock("./validation", () => ({
    cleanApplyAfterOptions: jest.fn(),
    validateApplyAfter: jest.fn(),
}));

jest.mock("./rootStage", () => ({
    isBase2EditGroupEnabled: jest.fn().mockReturnValue(false),
}));

const mockCleanApplyAfter = cleanApplyAfterOptions as jest.MockedFunction<
    typeof cleanApplyAfterOptions
>;
const mockValidateApplyAfter = validateApplyAfter as jest.MockedFunction<
    typeof validateApplyAfter
>;

let getStages: jest.MockedFunction<() => Stage[]>;
let saveStages: jest.MockedFunction<(stages: Stage[]) => void>;
let updateStageFromUi: jest.MockedFunction<
    (prefix: string, stage: Stage) => void
>;

beforeEach(() => {
    jest.useFakeTimers();
    getStages = jest.fn();
    saveStages = jest.fn();
    updateStageFromUi = jest.fn();
    jest.clearAllMocks();
});

afterEach(() => {
    jest.useRealTimers();
});

const fireStageInput = (
    api: ReturnType<typeof createObservers>,
    stageId: number,
    targetEl: HTMLElement,
) => {
    const editor = document.createElement("div");
    const wrap = document.createElement("div");
    wrap.dataset.base2editStageId = String(stageId);
    wrap.appendChild(targetEl);
    editor.appendChild(wrap);
    document.body.appendChild(editor);
    api.installStageChangeListener(editor);
    targetEl.dispatchEvent(new Event("input", { bubbles: true }));
    jest.runAllTimers();
};

describe("syncSingleStageFromUi (via installStageChangeListener + debounce)", () => {
    it("does not call updateStageFromUi or saveStages when stageId is 0 (idx -1)", () => {
        getStages.mockReturnValue([makeStage()]);
        const api = createObservers({
            getStages,
            saveStages,
            updateStageFromUi,
        });
        const target = document.createElement("input");
        target.id = "some-input";
        fireStageInput(api, 0, target);
        expect(updateStageFromUi).not.toHaveBeenCalled();
        expect(saveStages).not.toHaveBeenCalled();
    });

    it("does not call updateStageFromUi or saveStages when stageId exceeds stages.length", () => {
        getStages.mockReturnValue([makeStage()]);
        const api = createObservers({
            getStages,
            saveStages,
            updateStageFromUi,
        });
        const target = document.createElement("input");
        target.id = "some-input";
        fireStageInput(api, 2, target);
        expect(updateStageFromUi).not.toHaveBeenCalled();
        expect(saveStages).not.toHaveBeenCalled();
    });

    it("calls updateStageFromUi and saveStages when stageId is valid and isApplyAfter is false", () => {
        const stage = makeStage();
        getStages.mockReturnValue([stage]);
        const api = createObservers({
            getStages,
            saveStages,
            updateStageFromUi,
        });
        const target = document.createElement("input");
        target.id = "some-input";
        fireStageInput(api, 1, target);
        expect(updateStageFromUi).toHaveBeenCalledTimes(1);
        expect(updateStageFromUi).toHaveBeenCalledWith(
            "base2edit_stage_1_",
            stage,
        );
        expect(saveStages).toHaveBeenCalledTimes(1);
        expect(saveStages).toHaveBeenCalledWith([stage]);
        expect(mockCleanApplyAfter).not.toHaveBeenCalled();
        expect(mockValidateApplyAfter).not.toHaveBeenCalled();
    });

    it("calls cleanApplyAfterOptions and validateApplyAfter with correct stageIds and stageId when target is inside applyafter element", () => {
        getStages.mockReturnValue([makeStage(), makeStage()]);
        const api = createObservers({
            getStages,
            saveStages,
            updateStageFromUi,
        });
        const applyafter = addSelect(
            "base2edit_stage_1_applyafter",
            "Refiner",
            ["Refiner", "Edit Stage 1"],
        );
        fireStageInput(api, 1, applyafter);
        expect(mockCleanApplyAfter).toHaveBeenCalledTimes(1);
        expect(mockCleanApplyAfter.mock.calls[0][0]).toBe(applyafter);
        expect(mockCleanApplyAfter.mock.calls[0][1]).toEqual([0, 1, 2]);
        expect(mockCleanApplyAfter.mock.calls[0][2]).toBe(1);
        expect(mockValidateApplyAfter).toHaveBeenCalledTimes(1);
        expect(mockValidateApplyAfter).toHaveBeenCalledWith(
            "base2edit_stage_1_",
            [0, 1, 2],
            1,
        );
    });

    it("stageIds always starts with 0 (root stage prepended)", () => {
        getStages.mockReturnValue([makeStage(), makeStage(), makeStage()]);
        const api = createObservers({
            getStages,
            saveStages,
            updateStageFromUi,
        });
        const applyafter = addSelect(
            "base2edit_stage_2_applyafter",
            "Refiner",
            ["Refiner"],
        );
        fireStageInput(api, 2, applyafter);
        expect(mockCleanApplyAfter.mock.calls[0][1]).toEqual([0, 1, 2, 3]);
        expect(mockCleanApplyAfter.mock.calls[0][2]).toBe(2);
    });
});
