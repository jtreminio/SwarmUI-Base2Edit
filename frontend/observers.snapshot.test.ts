import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { addInput, makeStage } from "./__test_helpers__";
import { createObservers } from "./observers";
import { isBase2EditGroupEnabled } from "./rootStage";
import type { Stage } from "./types";

jest.mock("./rootStage", () => ({ isBase2EditGroupEnabled: jest.fn() }));
const mockIsEnabled = isBase2EditGroupEnabled as jest.MockedFunction<
    typeof isBase2EditGroupEnabled
>;

beforeEach(() => {
    jest.useFakeTimers();
    mockIsEnabled.mockReturnValue(true);
    addInput("input_editstages", "[]");
    delete window.base2editStageRegistry;
});

afterEach(() => {
    jest.useRealTimers();
});

const captureStageChanges = () => {
    const events: CustomEvent[] = [];
    const listener = (e: Event) => events.push(e as CustomEvent);
    document.addEventListener("base2edit:stages-changed", listener);
    return {
        events,
        dispose: () =>
            document.removeEventListener("base2edit:stages-changed", listener),
    };
};

describe("buildStageSnapshot via publishStageAvailability", () => {
    it("emits enabled=false, stageCount=0, refs=[] when group is disabled", () => {
        mockIsEnabled.mockReturnValue(false);
        const getStages = jest.fn<() => Stage[]>().mockReturnValue([]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });
        const cap = captureStageChanges();

        api.publishStageAvailability();

        expect(cap.events[0].detail.enabled).toBe(false);
        expect(cap.events[0].detail.stageCount).toBe(0);
        expect(cap.events[0].detail.refs).toEqual([]);

        cap.dispose();
    });

    it("emits stageCount=1 and refs=['edit0'] when enabled with 0 stages", () => {
        mockIsEnabled.mockReturnValue(true);
        const getStages = jest.fn<() => Stage[]>().mockReturnValue([]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });
        const cap = captureStageChanges();

        api.publishStageAvailability();

        expect(cap.events[0].detail.stageCount).toBe(1);
        expect(cap.events[0].detail.refs).toEqual(["edit0"]);

        cap.dispose();
    });

    it("emits stageCount=4 and refs=['edit0','edit1','edit2','edit3'] when enabled with 3 stages", () => {
        mockIsEnabled.mockReturnValue(true);
        const getStages = jest
            .fn<() => Stage[]>()
            .mockReturnValue([makeStage(), makeStage(), makeStage()]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });
        const cap = captureStageChanges();

        api.publishStageAvailability();

        expect(cap.events[0].detail.stageCount).toBe(4);
        expect(cap.events[0].detail.refs).toEqual([
            "edit0",
            "edit1",
            "edit2",
            "edit3",
        ]);

        cap.dispose();
    });
});

describe("cloneStageSnapshot deep-copy independence", () => {
    it("getSnapshot returns independent copies — mutating refs of first result does not affect second call", () => {
        mockIsEnabled.mockReturnValue(true);
        const getStages = jest
            .fn<() => Stage[]>()
            .mockReturnValue([makeStage()]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });

        api.publishStageAvailability();

        expect(window.base2editStageRegistry).toBeDefined();
        const snap1 = window.base2editStageRegistry.getSnapshot();
        snap1.refs.push("EXTRA");

        const snap2 = window.base2editStageRegistry.getSnapshot();

        expect(snap2.refs).toEqual(["edit0", "edit1"]);
    });
});

describe("markPersisted + startPublishedStageSync change detection", () => {
    it("does not publish on tick when markPersisted matches current values", () => {
        mockIsEnabled.mockReturnValue(true);
        const input = document.getElementById(
            "input_editstages",
        ) as HTMLInputElement;
        input.value = '[{"x":1}]';
        const getStages = jest.fn<() => Stage[]>().mockReturnValue([]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });

        api.markPersisted('[{"x":1}]', true);
        api.startPublishedStageSync();

        const cap = captureStageChanges();
        jest.advanceTimersByTime(300);

        expect(cap.events.length).toBe(0);

        cap.dispose();
    });

    it("publishes when JSON changes between ticks", () => {
        mockIsEnabled.mockReturnValue(true);
        const input = document.getElementById(
            "input_editstages",
        ) as HTMLInputElement;
        const getStages = jest.fn<() => Stage[]>().mockReturnValue([]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });

        api.markPersisted("[]", true);
        api.startPublishedStageSync();

        const cap = captureStageChanges();
        input.value = '[{"x":1}]';
        jest.advanceTimersByTime(160);

        expect(cap.events.length).toBe(1);

        cap.dispose();
    });

    it("publishes when enabled flag changes between ticks", () => {
        mockIsEnabled.mockReturnValue(true);
        const getStages = jest.fn<() => Stage[]>().mockReturnValue([]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });

        api.markPersisted("[]", true);
        api.startPublishedStageSync();

        mockIsEnabled.mockReturnValue(false);

        const cap = captureStageChanges();
        jest.advanceTimersByTime(200);

        expect(cap.events.length).toBeGreaterThanOrEqual(1);
        expect(cap.events[cap.events.length - 1].detail.enabled).toBe(false);

        cap.dispose();
    });

    it("startPublishedStageSync is idempotent — calling twice creates only one interval", () => {
        mockIsEnabled.mockReturnValue(true);
        const getStages = jest.fn<() => Stage[]>().mockReturnValue([]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });

        const setIntervalSpy = jest.spyOn(globalThis, "setInterval");

        api.startPublishedStageSync();
        api.startPublishedStageSync();

        expect(setIntervalSpy.mock.calls.length).toBe(1);

        setIntervalSpy.mockRestore();
    });

    it("does not publish again when state stays unchanged after a publish", () => {
        mockIsEnabled.mockReturnValue(true);
        const input = document.getElementById(
            "input_editstages",
        ) as HTMLInputElement;
        const getStages = jest.fn<() => Stage[]>().mockReturnValue([]);
        const api = createObservers({
            getStages: getStages as () => Stage[],
            saveStages: jest.fn(),
            updateStageFromUi: jest.fn(),
        });

        api.markPersisted("[]", true);
        api.startPublishedStageSync();

        const cap = captureStageChanges();
        input.value = '[{"x":1}]';
        jest.advanceTimersByTime(160);

        expect(cap.events.length).toBe(1);

        jest.advanceTimersByTime(300);

        expect(cap.events.length).toBe(1);

        cap.dispose();
    });
});
