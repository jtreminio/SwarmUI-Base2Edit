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

(globalThis as any).promptTabComplete = {
    registerPrefix: jest.fn(),
};

import { tryRegisterStageEditor } from "./main";

describe("tryRegisterStageEditor", () => {
    beforeEach(() => {
        mockInit.mockReset();
    });

    it("returns false when postParamBuildSteps is undefined", () => {
        delete (globalThis as { postParamBuildSteps?: unknown })
            .postParamBuildSteps;
        expect(tryRegisterStageEditor()).toBe(false);
    });

    it("returns false when postParamBuildSteps is null", () => {
        (globalThis as { postParamBuildSteps?: unknown }).postParamBuildSteps =
            null as unknown as undefined;
        expect(tryRegisterStageEditor()).toBe(false);
    });

    it("returns false when postParamBuildSteps is a plain object", () => {
        (globalThis as { postParamBuildSteps?: unknown }).postParamBuildSteps =
            {} as unknown as undefined;
        expect(tryRegisterStageEditor()).toBe(false);
    });

    it("returns false when postParamBuildSteps is a number", () => {
        (globalThis as { postParamBuildSteps?: unknown }).postParamBuildSteps =
            42 as unknown as undefined;
        expect(tryRegisterStageEditor()).toBe(false);
    });

    it("returns true when postParamBuildSteps is an array", () => {
        expect(tryRegisterStageEditor()).toBe(true);
    });

    it("pushes exactly one callback when postParamBuildSteps is an array", () => {
        tryRegisterStageEditor();
        expect(
            ((globalThis as any).postParamBuildSteps as unknown[]).length,
        ).toBe(1);
    });

    it("pushed callback invokes editor.init() when called", () => {
        tryRegisterStageEditor();
        const cb = (
            (globalThis as any).postParamBuildSteps as Array<() => void>
        )[0];
        cb();
        expect(mockInit).toHaveBeenCalledTimes(1);
    });

    it("pushed callback swallows exceptions thrown by editor.init()", () => {
        mockInit.mockImplementation(() => {
            throw new Error("boom");
        });
        tryRegisterStageEditor();
        const cb = (
            (globalThis as any).postParamBuildSteps as Array<() => void>
        )[0];
        expect(() => cb()).not.toThrow();
    });
});
