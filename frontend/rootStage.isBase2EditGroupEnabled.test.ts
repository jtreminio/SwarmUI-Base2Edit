import { describe, expect, it } from "@jest/globals";
import { addCheckbox } from "./__test_helpers__";
import { isBase2EditGroupEnabled } from "./rootStage";

describe("isBase2EditGroupEnabled", () => {
    it("returns true when toggler element is absent", () => {
        expect(isBase2EditGroupEnabled()).toBe(true);
    });

    it("returns true when toggler is present and checked", () => {
        addCheckbox("input_group_content_baseedit_toggle", true);
        expect(isBase2EditGroupEnabled()).toBe(true);
    });

    it("returns false when toggler is present and unchecked", () => {
        addCheckbox("input_group_content_baseedit_toggle", false);
        expect(isBase2EditGroupEnabled()).toBe(false);
    });
});
