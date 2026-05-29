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
import { addToggleStageBtnListener } from "./renderStages";
import { getRootStage } from "./rootStage";
import type { RootStage, Stage } from "./types";

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
        btn.dataset.base2editAction = "toggle-stage";
        wrap.appendChild(btn);
        list.appendChild(wrap);
        buttons.push(btn);
    }

    return { list, buttons };
};

const makeDeps = (stages: Stage[]) => ({
    serializeStagesFromUi: jest.fn(),
    getStages: jest.fn().mockReturnValue(stages),
    saveStages: jest.fn(),
});

describe("addToggleStageBtnListener", () => {
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

    it("collapses an expanded (undefined) stage when its toggle button is clicked", () => {
        const { list, buttons } = buildList(3);
        const stages = [makeStage(), makeStage(), makeStage()];
        const deps = makeDeps(stages);
        addToggleStageBtnListener(list, deps as unknown as RenderDeps);
        buttons[1].dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).toHaveBeenCalledTimes(1);
        const saved = deps.saveStages.mock.calls[0]?.[0] as Stage[];
        expect(saved[1].expanded).toBe(false);
        expect(saved[0].expanded).toBeUndefined();
        expect(saved[2].expanded).toBeUndefined();
    });

    it("re-expands a collapsed stage", () => {
        const { list, buttons } = buildList(1);
        const stages = [makeStage({ expanded: false })];
        const deps = makeDeps(stages);
        addToggleStageBtnListener(list, deps as unknown as RenderDeps);
        buttons[0].dispatchEvent(new MouseEvent("click", { bubbles: true }));
        const saved = deps.saveStages.mock.calls[0]?.[0] as Stage[];
        expect(saved[0].expanded).toBe(true);
    });

    it("serializes in-progress edits before reading stages", () => {
        const { list, buttons } = buildList(1);
        const deps = makeDeps([makeStage()]);
        addToggleStageBtnListener(list, deps as unknown as RenderDeps);
        buttons[0].dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(
            deps.serializeStagesFromUi.mock.invocationCallOrder[0],
        ).toBeLessThan(deps.getStages.mock.invocationCallOrder[0]);
    });

    it("does nothing when the clicked element is not a toggle-stage button", () => {
        const list = document.createElement("div");
        document.body.appendChild(list);
        const btn = document.createElement("button");
        btn.dataset.base2editAction = "remove-stage";
        list.appendChild(btn);
        const deps = makeDeps([makeStage()]);
        addToggleStageBtnListener(list, deps as unknown as RenderDeps);
        btn.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).not.toHaveBeenCalled();
    });

    it("does not save when the toggle button has no [data-base2edit-stage-id] ancestor", () => {
        const list = document.createElement("div");
        document.body.appendChild(list);
        const btn = document.createElement("button");
        btn.dataset.base2editAction = "toggle-stage";
        list.appendChild(btn);
        const deps = makeDeps([makeStage()]);
        addToggleStageBtnListener(list, deps as unknown as RenderDeps);
        btn.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).not.toHaveBeenCalled();
    });

    // Mirrors the real header markup: the whole header is the toggle zone, with
    // the skip/delete buttons nested inside it.
    const buildHeaderStage = (): {
        list: HTMLElement;
        title: HTMLElement;
        skipBtn: HTMLElement;
    } => {
        const list = document.createElement("div");
        document.body.appendChild(list);
        const wrap = document.createElement("div");
        wrap.dataset.base2editStageId = "1";
        const header = document.createElement("span");
        header.dataset.base2editAction = "toggle-stage";
        const title = document.createElement("span");
        title.textContent = "Edit Stage 1";
        const skipBtn = document.createElement("button");
        skipBtn.dataset.base2editAction = "skip-stage";
        header.append(title, skipBtn);
        wrap.appendChild(header);
        list.appendChild(wrap);
        return { list, title, skipBtn };
    };

    it("toggles when a non-button part of the header (e.g. the title) is clicked", () => {
        const { list, title } = buildHeaderStage();
        const deps = makeDeps([makeStage()]);
        addToggleStageBtnListener(list, deps as unknown as RenderDeps);
        title.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        const saved = deps.saveStages.mock.calls[0]?.[0] as Stage[];
        expect(saved[0].expanded).toBe(false);
    });

    it("does not toggle when a skip button inside the header is clicked", () => {
        const { list, skipBtn } = buildHeaderStage();
        const deps = makeDeps([makeStage()]);
        addToggleStageBtnListener(list, deps as unknown as RenderDeps);
        skipBtn.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(deps.saveStages).not.toHaveBeenCalled();
    });
});
