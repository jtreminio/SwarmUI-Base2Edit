import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { makeStage } from "./__test_helpers__";
import type { RenderDeps } from "./renderStages";
import { addRemoveBtnListener } from "./renderStages";
import { getRootStage } from "./rootStage";
import type { RootStage } from "./types";

jest.mock("./rootStage", () => ({ getRootStage: jest.fn() }));
const mockGetRootStage = getRootStage as jest.MockedFunction<
    typeof getRootStage
>;

type GlobalWithHelpers = typeof globalThis & {
    // biome-ignore lint/suspicious/noExplicitAny: global test helper
    getHtmlForParam?: any;
    // biome-ignore lint/suspicious/noExplicitAny: global test helper
    doToggleEnable?: any;
    // biome-ignore lint/suspicious/noExplicitAny: global test helper
    Utils?: any;
};
const globals = globalThis as GlobalWithHelpers;

const makeRootStageStub = () => ({
    refineOnly: { checked: true },
    control: { min: 0, max: 1, step: 0.05 },
    upscale: { value: "1", min: 1, max: 4, step: 0.25 },
    upscaleMethod: {
        options: [{ value: "lanczos", label: "Lanczos" }],
        value: "lanczos",
    },
    model: { options: [{ value: "base-model" }] },
    vae: { options: [{ value: "auto", label: "Auto" }], value: "auto" },
    steps: { min: 1, max: 150, step: 1 },
    cfgScale: { value: "7", min: 1, max: 30, step: 0.5 },
    sampler: { options: [{ value: "euler", label: "Euler" }], value: "euler" },
    scheduler: {
        options: [{ value: "karras", label: "Karras" }],
        value: "karras",
    },
    keepPreEditImage: { checked: false },
});

const buildList = (
    count: number,
): { list: HTMLElement; buttons: HTMLButtonElement[] } => {
    const list = document.createElement("div");
    document.body.appendChild(list);
    const buttons: HTMLButtonElement[] = [];

    for (let i = 1; i <= count; i++) {
        const wrap = document.createElement("div");
        wrap.dataset.base2editStageId = String(i);
        const btn = document.createElement("button");
        btn.dataset.base2editAction = "remove-stage";
        wrap.appendChild(btn);
        list.appendChild(wrap);
        buttons.push(btn);
    }

    return { list, buttons };
};

const makeDeps = (stages = [makeStage(), makeStage(), makeStage()]) => ({
    serializeStagesFromUi: jest.fn(),
    getStages: jest.fn().mockReturnValue(stages),
    saveStages: jest.fn(),
});

describe("addRemoveBtnListener", () => {
    beforeEach(() => {
        mockGetRootStage.mockReturnValue(
            makeRootStageStub() as unknown as RootStage,
        );
        globals.getHtmlForParam = jest
            .fn()
            .mockReturnValue({ html: "", runnable: jest.fn() });
        globals.doToggleEnable = jest.fn();
        globals.Utils = {
            getInputElement: jest.fn(),
            getSelectElement: jest.fn(),
        };
    });

    afterEach(() => {
        document.body.innerHTML = "";
        delete globals.getHtmlForParam;
        delete globals.doToggleEnable;
        delete globals.Utils;
    });

    it("removes the stage at index stageId-1 when button[data-base2edit-stage-id='2'] is clicked", () => {
        const { list, buttons } = buildList(3);
        const stages = [
            makeStage({ steps: 1 }),
            makeStage({ steps: 2 }),
            makeStage({ steps: 3 }),
        ];
        const deps = makeDeps(stages);
        addRemoveBtnListener(list, deps as unknown as RenderDeps);
        buttons[1].dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).toHaveBeenCalledTimes(1);
        const savedStages = deps.saveStages.mock.calls[0]?.[0] as unknown[];
        expect(savedStages).toHaveLength(2);
        // biome-ignore lint/suspicious/noExplicitAny: mock result type
        expect((savedStages[0] as any).steps).toBe(1);
        // biome-ignore lint/suspicious/noExplicitAny: mock result type
        expect((savedStages[1] as any).steps).toBe(3);
    });

    it("removes the stage at index 0 when data-base2edit-stage-id='1'", () => {
        const { list, buttons } = buildList(2);
        const stages = [makeStage({ steps: 10 }), makeStage({ steps: 20 })];
        const deps = makeDeps(stages);
        addRemoveBtnListener(list, deps as unknown as RenderDeps);
        buttons[0].dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).toHaveBeenCalledTimes(1);
        const savedStages = deps.saveStages.mock.calls[0]?.[0] as unknown[];
        expect(savedStages).toHaveLength(1);
        // biome-ignore lint/suspicious/noExplicitAny: mock result type
        expect((savedStages[0] as any).steps).toBe(20);
    });

    it("does not call saveStages when clicked element has no [data-base2edit-stage-id] ancestor", () => {
        const list = document.createElement("div");
        document.body.appendChild(list);
        const btn = document.createElement("button");
        btn.dataset.base2editAction = "remove-stage";
        list.appendChild(btn);
        const deps = makeDeps();
        addRemoveBtnListener(list, deps as unknown as RenderDeps);
        btn.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).not.toHaveBeenCalled();
    });

    it("does not call saveStages when the clicked element is not a remove-stage button", () => {
        const { list } = buildList(1);
        const wrap = list.children[0] as HTMLElement;
        const deps = makeDeps();
        addRemoveBtnListener(list, deps as unknown as RenderDeps);
        wrap.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).not.toHaveBeenCalled();
    });

    it("calls serializeStagesFromUi before getStages", () => {
        const { list, buttons } = buildList(1);
        const deps = makeDeps();
        addRemoveBtnListener(list, deps as unknown as RenderDeps);
        buttons[0].dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(
            deps.serializeStagesFromUi.mock.invocationCallOrder[0],
        ).toBeLessThan(deps.getStages.mock.invocationCallOrder[0]);
    });

    it("does not call saveStages when data-base2edit-stage-id is empty string", () => {
        const list = document.createElement("div");
        document.body.appendChild(list);
        const wrap = document.createElement("div");
        wrap.dataset.base2editStageId = "";
        const btn = document.createElement("button");
        btn.dataset.base2editAction = "remove-stage";
        wrap.appendChild(btn);
        list.appendChild(wrap);
        const deps = makeDeps();
        addRemoveBtnListener(list, deps as unknown as RenderDeps);
        btn.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).not.toHaveBeenCalled();
    });
});
