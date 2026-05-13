import { describe, expect, it } from "@jest/globals";
import { Utils } from "../Utils";
import {
    addCheckbox,
    addInput,
    addSelect,
    addToggle,
    makeStage,
} from "./index";

describe("test scaffolding smoke", () => {
    it("addInput exposes the element via Utils.getInputElement", () => {
        addInput("smoke_input", "hello");
        expect(Utils.getInputElement("smoke_input")?.value).toBe("hello");
    });

    it("addCheckbox preserves checked state", () => {
        addCheckbox("smoke_cb", true);
        expect(Utils.getInputElement("smoke_cb")?.checked).toBe(true);
    });

    it("addToggle creates id with _toggle suffix", () => {
        addToggle("smoke_x", false);
        expect(Utils.getInputElement("smoke_x_toggle")?.checked).toBe(false);
    });

    it("addSelect appends an option for the chosen value", () => {
        const sel = addSelect("smoke_sel", "b", ["a", "b", "c"]);
        expect(Utils.getSelectElement("smoke_sel")?.value).toBe("b");
        expect(sel.options.length).toBe(3);
    });

    it("loads SwarmUI's util.js into the test environment", () => {
        // findParentOfClass comes from src/wwwroot/js/util.js via jest.setup.js
        expect(
            typeof (globalThis as Record<string, unknown>).findParentOfClass,
        ).toBe("function");
    });

    it("makeStage returns a Stage with overrides applied", () => {
        const s = makeStage({ steps: 42, vae: "myvae" });
        expect(s.steps).toBe(42);
        expect(s.vae).toBe("myvae");
        expect(s.applyAfter).toBe("Refiner");
    });

    it("isolates document.body between tests", () => {
        expect(document.getElementById("smoke_input")).toBeNull();
    });
});
