import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { makeStage } from "./__test_helpers__";
import { buildFieldsForStage } from "./renderStages";
import { getRootStage } from "./rootStage";
import type { RootStage } from "./types";

jest.mock("./rootStage", () => ({ getRootStage: jest.fn() }));
const mockGetRootStage = getRootStage as jest.MockedFunction<
    typeof getRootStage
>;

// biome-ignore lint/suspicious/noExplicitAny: global test helper
type GlobalWithHtmlForParam = typeof globalThis & { getHtmlForParam?: any };
const globals = globalThis as GlobalWithHtmlForParam;

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
});

beforeEach(() => {
    mockGetRootStage.mockReturnValue(
        makeRootStageStub() as unknown as RootStage,
    );
    globals.getHtmlForParam = jest
        .fn()
        .mockReturnValue({ html: "", runnable: jest.fn() });
});

afterEach(() => {
    delete globals.getHtmlForParam;
});

const callFor = (id: string) =>
    // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
    globals.getHtmlForParam!.mock.calls.find(
        // biome-ignore lint/suspicious/noExplicitAny: mock call args
        ([param]: any[]) => (param as Record<string, unknown>).id === id,
    )?.[0] as Record<string, unknown> | undefined;

describe("buildFieldsForStage", () => {
    it("returns 12 parts and calls getHtmlForParam 12 times", () => {
        const stage = makeStage({ applyAfter: "Refiner" });
        const applyValues = ["Refiner", "Hires"];

        const result = buildFieldsForStage(stage, "prefix_", applyValues);

        expect(result.length).toBe(12);
        // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
        expect(globals.getHtmlForParam!.mock.calls.length).toBe(12);
    });

    it("uses stage.applyAfter as default when it is in applyValues", () => {
        const stage = makeStage({ applyAfter: "Hires" });
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("applyafter")?.default).toBe("Hires");
    });

    it("falls back to applyValues[0] when stage.applyAfter is not in applyValues", () => {
        const stage = makeStage({ applyAfter: "OldStage" });
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("applyafter")?.default).toBe("Refiner");
    });

    it("uses rootStage.refineOnly.checked when stage.refineOnly is undefined", () => {
        const stage = makeStage({ refineOnly: undefined });
        mockGetRootStage.mockReturnValue({
            ...makeRootStageStub(),
            refineOnly: { checked: false },
        } as unknown as RootStage);
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("editrefineonly")?.default).toBe("false");
    });

    it("uses stage.refineOnly when explicitly set", () => {
        const stage = makeStage({ refineOnly: true });
        mockGetRootStage.mockReturnValue({
            ...makeRootStageStub(),
            refineOnly: { checked: false },
        } as unknown as RootStage);
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("editrefineonly")?.default).toBe("true");
    });

    it("uses rootStage.upscale.value when stage.upscale is null", () => {
        const stage = makeStage({ upscale: null as unknown as number });
        mockGetRootStage.mockReturnValue({
            ...makeRootStageStub(),
            upscale: { value: "2", min: 1, max: 4, step: 0.25 },
        } as unknown as RootStage);
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("editupscale")?.default).toBe("2");
    });

    it("uses rootStage.cfgScale.value when stage.cfgScale is null", () => {
        const stage = makeStage({ cfgScale: null });
        mockGetRootStage.mockReturnValue({
            ...makeRootStageStub(),
            cfgScale: { value: "8", min: 1, max: 30, step: 0.5 },
        } as unknown as RootStage);
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("editcfgscale")?.default).toBe("8");
    });

    it("uses rootStage.sampler.value when stage.sampler is null", () => {
        const stage = makeStage({ sampler: null });
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("editsampler")?.default).toBe("euler");
    });

    it("uses rootStage.scheduler.value when stage.scheduler is null", () => {
        const stage = makeStage({ scheduler: null });
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("editscheduler")?.default).toBe("karras");
    });

    it("uses rootStage.vae.value when stage.vae is null", () => {
        const stage = makeStage({ vae: null });
        const applyValues = ["Refiner", "Hires"];

        buildFieldsForStage(stage, "prefix_", applyValues);

        expect(callFor("editvae")?.default).toBe("auto");
    });

    it("uses rootStage.upscaleMethod.value when stage.upscaleMethod is null", () => {
        const stage = makeStage({ upscaleMethod: null as unknown as string });
        buildFieldsForStage(stage, "pfx_", ["Refiner"]);
        expect(callFor("editupscalemethod")?.default).toBe("lanczos");
    });

    it("passes prefix as second argument to every getHtmlForParam call", () => {
        const stage = makeStage({ applyAfter: "Refiner" });
        const applyValues = ["Refiner", "Hires"];
        const prefix = "base2edit_stage_3_";

        buildFieldsForStage(stage, prefix, applyValues);

        // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
        for (let i = 0; i < globals.getHtmlForParam!.mock.calls.length; i++) {
            // biome-ignore lint/style/noNonNullAssertion: set in beforeEach
            expect(globals.getHtmlForParam!.mock.calls[i][1]).toBe(prefix);
        }
    });
});
