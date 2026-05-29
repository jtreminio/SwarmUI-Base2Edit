import { beforeEach, describe, expect, it, jest } from "@jest/globals";

const mockInit = jest.fn();

jest.mock("./stageEditor", () => ({
    stageEditor: () => ({ init: mockInit, startGenerateWrapRetry: jest.fn() }),
}));

jest.mock("./imageButtons", () => ({
    createImageButtons: () => ({
        waitFor: jest.fn(),
    }),
}));

jest.mock("./promptPrefixes", () => ({
    registerB2EImagePrefix: jest.fn(),
    registerB2EPromptPrefix: jest.fn(),
    registerEditPromptPrefix: jest.fn(),
}));

jest.mock("./runEditOnly", () => ({
    runEditOnlyFromImage: jest.fn(),
}));

type TestGlobal = typeof globalThis & {
    promptTabComplete?: { registerPrefix: () => void };
    postParamBuildSteps?: (() => void)[];
};
const testGlobal = globalThis as TestGlobal;

testGlobal.promptTabComplete = {
    registerPrefix: jest.fn(),
};

import { tryRegisterStageEditor } from "./main";

describe("tryRegisterStageEditor", () => {
    beforeEach(() => {
        mockInit.mockReset();
    });

    it("returns false when postParamBuildSteps is undefined", () => {
        delete testGlobal.postParamBuildSteps;
        expect(tryRegisterStageEditor()).toBe(false);
    });

    it("returns false when postParamBuildSteps is null", () => {
        testGlobal.postParamBuildSteps = null;
        expect(tryRegisterStageEditor()).toBe(false);
    });

    it("returns false when postParamBuildSteps is a plain object", () => {
        testGlobal.postParamBuildSteps = {} as unknown as (() => void)[];
        expect(tryRegisterStageEditor()).toBe(false);
    });

    it("returns false when postParamBuildSteps is a number", () => {
        testGlobal.postParamBuildSteps = 42 as unknown as (() => void)[];
        expect(tryRegisterStageEditor()).toBe(false);
    });

    it("returns true when postParamBuildSteps is an array", () => {
        expect(tryRegisterStageEditor()).toBe(true);
    });

    it("pushes exactly one callback when postParamBuildSteps is an array", () => {
        tryRegisterStageEditor();
        expect(testGlobal.postParamBuildSteps.length).toBe(1);
    });

    it("pushed callback invokes editor.init() when called", () => {
        tryRegisterStageEditor();
        const cb = testGlobal.postParamBuildSteps[0];
        cb();
        expect(mockInit).toHaveBeenCalledTimes(1);
    });

    it("pushed callback swallows exceptions thrown by editor.init()", () => {
        mockInit.mockImplementation(() => {
            throw new Error("boom");
        });
        tryRegisterStageEditor();
        const cb = testGlobal.postParamBuildSteps[0];
        const logSpy = jest.spyOn(console, "log").mockImplementation(() => {});
        try {
            expect(() => cb()).not.toThrow();
        } finally {
            logSpy.mockRestore();
        }
    });
});
