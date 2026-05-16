import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { createImageButtons } from "./imageButtons";

type Globals = typeof globalThis & {
    buttonsForImage?: (
        fullsrc: string,
        src: string,
        metadata: unknown,
    ) => Array<{ label: string; title: string; onclick: () => void }>;
};
const globals = globalThis as Globals;

let originalButtonsForImage: typeof globals.buttonsForImage;

beforeEach(() => {
    jest.useFakeTimers();
    originalButtonsForImage = globals.buttonsForImage;
    delete globals.buttonsForImage;
    delete (
        window as Window & {
            base2editRunEditOnlyFromImage?: (src: string) => void;
        }
    ).base2editRunEditOnlyFromImage;
});

afterEach(() => {
    jest.useRealTimers();
    if (originalButtonsForImage) {
        globals.buttonsForImage = originalButtonsForImage;
    } else {
        delete globals.buttonsForImage;
    }
    delete (
        window as Window & {
            base2editRunEditOnlyFromImage?: (src: string) => void;
        }
    ).base2editRunEditOnlyFromImage;
});

describe("createImageButtons / init", () => {
    it("returns false when buttonsForImage is undefined", () => {
        const { init } = createImageButtons();
        expect(init(jest.fn())).toBe(false);
    });

    it("returns true and wraps buttonsForImage when it is defined", () => {
        const mockFn = jest
            .fn()
            .mockReturnValue([
                { label: "Orig", title: "Orig", onclick: jest.fn() },
            ]); // biome-ignore lint/suspicious/noExplicitAny: Jest mock type compatibility
        globals.buttonsForImage = mockFn as any;
        const original = globals.buttonsForImage;
        const { init } = createImageButtons();
        expect(init(jest.fn())).toBe(true);
        expect(globals.buttonsForImage !== original).toBe(true);
    });

    it("is idempotent — second init returns true without re-wrapping", () => {
        const mockFn = jest
            .fn()
            .mockReturnValue([
                { label: "Orig", title: "Orig", onclick: jest.fn() },
            ]); // biome-ignore lint/suspicious/noExplicitAny: Jest mock type compatibility
        globals.buttonsForImage = mockFn as any;
        const { init } = createImageButtons();
        init(jest.fn());
        const wrappedOnce = globals.buttonsForImage;
        init(jest.fn());
        expect(globals.buttonsForImage === wrappedOnce).toBe(true);
    });

    it("does not add button when window.base2editRunEditOnlyFromImage is absent", () => {
        const mockFn = jest
            .fn()
            .mockReturnValue([
                { label: "Orig", title: "Orig", onclick: jest.fn() },
            ]); // biome-ignore lint/suspicious/noExplicitAny: Jest mock type compatibility
        globals.buttonsForImage = mockFn as any;
        const { init } = createImageButtons();
        init(jest.fn());
        // biome-ignore lint/style/noNonNullAssertion: buttonsForImage guaranteed to exist after init
        const result = globals.buttonsForImage!("full.png", "foo.png", null);
        expect(result).toHaveLength(1);
    });

    it("adds Base2Edit button when window.base2editRunEditOnlyFromImage is present and src is an image", () => {
        const mockFn = jest
            .fn()
            .mockReturnValue([
                { label: "Orig", title: "Orig", onclick: jest.fn() },
            ]); // biome-ignore lint/suspicious/noExplicitAny: Jest mock type compatibility
        globals.buttonsForImage = mockFn as any;
        (
            window as Window & {
                base2editRunEditOnlyFromImage?: (src: string) => void;
            }
        ).base2editRunEditOnlyFromImage = jest.fn();
        const onRun = jest.fn();
        const { init } = createImageButtons();
        init(onRun);
        // biome-ignore lint/style/noNonNullAssertion: buttonsForImage guaranteed to exist after init
        const result = globals.buttonsForImage!("full.png", "foo.png", null);
        expect(result).toHaveLength(2);
        expect(result[1].label).toBe("Base2Edit");
        expect(result[1].title).toBe(
            "Runs an edit-only Base2Edit pass on this image",
        );
        result[1].onclick();
        expect(onRun).toHaveBeenCalledWith("foo.png");
    });

    it("does not add button for video src ('.mp4')", () => {
        const mockFn = jest
            .fn()
            .mockReturnValue([
                { label: "Orig", title: "Orig", onclick: jest.fn() },
            ]); // biome-ignore lint/suspicious/noExplicitAny: Jest mock type compatibility
        globals.buttonsForImage = mockFn as any;
        (
            window as Window & {
                base2editRunEditOnlyFromImage?: (src: string) => void;
            }
        ).base2editRunEditOnlyFromImage = jest.fn();
        const { init } = createImageButtons();
        init(jest.fn());
        // biome-ignore lint/style/noNonNullAssertion: buttonsForImage guaranteed to exist after init
        const result = globals.buttonsForImage!("full.mp4", "foo.mp4", null);
        expect(result).toHaveLength(1);
    });

    it("does not add button for audio src ('.mp3')", () => {
        const mockFn = jest
            .fn()
            .mockReturnValue([
                { label: "Orig", title: "Orig", onclick: jest.fn() },
            ]); // biome-ignore lint/suspicious/noExplicitAny: Jest mock type compatibility
        globals.buttonsForImage = mockFn as any;
        (
            window as Window & {
                base2editRunEditOnlyFromImage?: (src: string) => void;
            }
        ).base2editRunEditOnlyFromImage = jest.fn();
        const { init } = createImageButtons();
        init(jest.fn());
        // biome-ignore lint/style/noNonNullAssertion: buttonsForImage guaranteed to exist after init
        const result = globals.buttonsForImage!("full.mp3", "foo.mp3", null);
        expect(result).toHaveLength(1);
    });
});

describe("createImageButtons / waitFor", () => {
    it("clears interval on first tick when buttonsForImage is already available", () => {
        // biome-ignore lint/suspicious/noExplicitAny: Jest mock type compatibility
        globals.buttonsForImage = jest.fn().mockReturnValue([]) as any;
        (
            window as Window & {
                base2editRunEditOnlyFromImage?: (src: string) => void;
            }
        ).base2editRunEditOnlyFromImage = jest.fn();
        const clearIntervalSpy = jest.spyOn(globalThis, "clearInterval");
        const { waitFor } = createImageButtons();
        waitFor(jest.fn());
        jest.advanceTimersByTime(500);
        expect(clearIntervalSpy).toHaveBeenCalled();
        clearIntervalSpy.mockRestore();
    });

    it("keeps polling and clears once buttonsForImage becomes available", () => {
        const clearIntervalSpy = jest.spyOn(globalThis, "clearInterval");
        const { waitFor } = createImageButtons();
        waitFor(jest.fn());
        jest.advanceTimersByTime(500);
        expect(clearIntervalSpy).not.toHaveBeenCalled();
        // biome-ignore lint/suspicious/noExplicitAny: Jest mock type compatibility
        globals.buttonsForImage = jest.fn().mockReturnValue([]) as any;
        jest.advanceTimersByTime(500);
        expect(clearIntervalSpy).toHaveBeenCalled();
        clearIntervalSpy.mockRestore();
    });
});
