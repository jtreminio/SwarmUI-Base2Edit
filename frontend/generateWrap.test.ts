import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { createGenerateWrap } from "./generateWrap";
import { isBase2EditGroupEnabled } from "./rootStage";
import type { Stage } from "./types";
import { validateStages } from "./validation";

jest.mock("./rootStage", () => ({ isBase2EditGroupEnabled: jest.fn() }));
jest.mock("./validation", () => ({ validateStages: jest.fn() }));

const mockIsEnabled = isBase2EditGroupEnabled as jest.MockedFunction<
    typeof isBase2EditGroupEnabled
>;
const mockValidate = validateStages as jest.MockedFunction<
    typeof validateStages
>;

type GlobalWithMainGen = typeof globalThis & {
    mainGenHandler?: { doGenerate?: unknown };
    showError?: (msg: string) => void;
};

const globals = globalThis as GlobalWithMainGen;

beforeEach(() => {
    jest.useFakeTimers();
    globals.mainGenHandler = { doGenerate: jest.fn() };
    globals.showError = jest.fn();
    mockIsEnabled.mockReturnValue(true);
    mockValidate.mockReturnValue([]);
});

afterEach(() => {
    jest.useRealTimers();
    delete globals.mainGenHandler;
    delete globals.showError;
});

describe("createGenerateWrap", () => {
    describe("tryWrap happy path", () => {
        it("patches mainGenHandler.doGenerate and sets __base2editWrapped on first call", () => {
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            const originalFn = globals.mainGenHandler!.doGenerate;
            const { tryWrap } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: jest.fn(),
            });

            tryWrap();

            expect(
                // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
                // biome-ignore lint/suspicious/noExplicitAny: accessing dynamic property
                (globals.mainGenHandler!.doGenerate as any).__base2editWrapped,
            ).toBe(true);
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            expect(globals.mainGenHandler!.doGenerate).not.toBe(originalFn);
        });

        it("is a no-op on second call — does not re-patch", () => {
            const { tryWrap } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: jest.fn(),
            });

            tryWrap();
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            const firstReference = globals.mainGenHandler!.doGenerate;

            tryWrap();
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            const secondReference = globals.mainGenHandler!.doGenerate;

            expect(firstReference).toBe(secondReference);
        });
    });

    describe("tryWrap guards", () => {
        it("does not throw when mainGenHandler is undefined", () => {
            globals.mainGenHandler = undefined;
            const { tryWrap } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: jest.fn(),
            });

            expect(() => tryWrap()).not.toThrow();
        });

        it("does not throw when mainGenHandler is null", () => {
            // biome-ignore lint/suspicious/noExplicitAny: test setup
            globals.mainGenHandler = null as any;
            const { tryWrap } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: jest.fn(),
            });

            expect(() => tryWrap()).not.toThrow();
        });

        it("does not throw when mainGenHandler.doGenerate is not a function", () => {
            globals.mainGenHandler = { doGenerate: "notafunction" };
            const originalDoGenerate = globals.mainGenHandler.doGenerate;
            const { tryWrap } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: jest.fn(),
            });

            expect(() => tryWrap()).not.toThrow();
            expect(globals.mainGenHandler.doGenerate).toBe(originalDoGenerate);
        });
    });

    describe("Patched doGenerate disabled", () => {
        it("calls original with original args and skips validation when disabled", () => {
            mockIsEnabled.mockReturnValue(false);
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            const originalFn = globals.mainGenHandler!.doGenerate;
            const mockGetStages = jest.fn<() => Stage[]>().mockReturnValue([]);
            const mockSerializeStages = jest.fn();

            const { tryWrap } = createGenerateWrap({
                getStages: mockGetStages,
                serializeStagesFromUi: mockSerializeStages,
            });

            tryWrap();
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            // biome-ignore lint/suspicious/noExplicitAny: calling dynamic function
            (globals.mainGenHandler!.doGenerate as any)("arg1", "arg2");

            expect(originalFn).toHaveBeenCalledWith("arg1", "arg2");
            expect(mockValidate).not.toHaveBeenCalled();
            expect(mockSerializeStages).not.toHaveBeenCalled();
        });
    });

    describe("Patched doGenerate validation errors", () => {
        it("calls showError with the first error and does not call original when validation fails", () => {
            mockValidate.mockReturnValue([
                "Stage X requires a model",
                "other error",
            ]);
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            const originalFn = globals.mainGenHandler!.doGenerate;
            const mockSerializeStages = jest.fn();

            const { tryWrap } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: mockSerializeStages,
            });

            tryWrap();
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            // biome-ignore lint/suspicious/noExplicitAny: calling dynamic function
            (globals.mainGenHandler!.doGenerate as any)();

            expect(mockSerializeStages).toHaveBeenCalledTimes(1);
            expect(globals.showError).toHaveBeenCalledWith(
                "Stage X requires a model",
            );
            expect(originalFn).not.toHaveBeenCalled();
        });
    });

    describe("Patched doGenerate valid", () => {
        it("calls serializeStagesFromUi then original when validation passes", () => {
            mockValidate.mockReturnValue([]);
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            const originalFn = globals.mainGenHandler!.doGenerate;
            const mockSerializeStages = jest.fn();

            const { tryWrap } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: mockSerializeStages,
            });

            tryWrap();
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            // biome-ignore lint/suspicious/noExplicitAny: calling dynamic function
            (globals.mainGenHandler!.doGenerate as any)("x");

            expect(mockSerializeStages).toHaveBeenCalled();
            expect(originalFn).toHaveBeenCalledWith("x");
            expect(globals.showError).not.toHaveBeenCalled();
            expect(
                mockSerializeStages.mock.invocationCallOrder[0],
            ).toBeLessThan(
                (originalFn as jest.Mock).mock.invocationCallOrder[0],
            );
        });
    });

    describe("startRetry", () => {
        it("does not double-stack intervals when called twice", () => {
            const setIntervalSpy = jest.spyOn(globalThis, "setInterval");
            const clearIntervalSpy = jest.spyOn(globalThis, "clearInterval");

            const { startRetry } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: jest.fn(),
            });

            startRetry();
            startRetry();

            expect(setIntervalSpy).toHaveBeenCalledTimes(1);

            // Advance timers so the check callback runs
            jest.advanceTimersByTime(250);

            // Verify clearInterval was called exactly once (not twice)
            expect(clearIntervalSpy).toHaveBeenCalledTimes(1);

            setIntervalSpy.mockRestore();
            clearIntervalSpy.mockRestore();
        });

        it("clears its interval once tryWrap succeeds", () => {
            const clearIntervalSpy = jest.spyOn(globalThis, "clearInterval");

            const { startRetry } = createGenerateWrap({
                getStages: jest.fn<() => Stage[]>().mockReturnValue([]),
                serializeStagesFromUi: jest.fn(),
            });

            startRetry(100);
            expect(
                // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
                // biome-ignore lint/suspicious/noExplicitAny: accessing dynamic property
                (globals.mainGenHandler!.doGenerate as any).__base2editWrapped,
            ).toBe(true);

            jest.advanceTimersByTime(500);

            expect(clearIntervalSpy).toHaveBeenCalled();

            clearIntervalSpy.mockRestore();
        });
    });
});
