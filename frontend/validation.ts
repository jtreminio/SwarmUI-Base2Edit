import type { Stage } from "./types";
import { Utils } from "./Utils";

export const isMissingStageRef = (
    applyAfter: string,
    stageIds: number[],
): boolean => {
    const m = applyAfter.match(/^Edit Stage (\d+)$/);
    if (!m) {
        return false;
    }

    return !stageIds.includes(parseInt(m[1], 10));
};

export const validateStages = (stages: Stage[]): string[] => {
    const errors: string[] = [];

    for (let i = 0; i < stages.length; i++) {
        const stage = stages[i];
        const stageId = i + 1;
        const a = stage.applyAfter;
        const m = `${a}`.match(/^Edit Stage (\d+)$/);

        if (m) {
            const refId = parseInt(m[1], 10);
            if (refId >= stageId) {
                errors.push(
                    `Base2Edit: Edit Stage ${stageId} cannot Apply After "${a}" (must reference an earlier stage).`,
                );
            }
        }
    }

    return errors;
};

export const buildApplyAfterList = (
    stageIds: number[],
    stageId: number,
    currentVal: string,
): string[] => {
    const values = ["Refiner"];
    const refs = [...stageIds]
        .filter((id) => id < stageId)
        .sort((a, b) => a - b)
        .map((id) => `Edit Stage ${id}`);
    values.push(...refs);

    if (currentVal && !values.includes(currentVal)) {
        values.unshift(currentVal);
    }

    return values;
};

export const cleanApplyAfterOptions = (
    applyElem: HTMLSelectElement,
    stageIds: number[],
    stageId: number,
): void => {
    const selectedVal = `${applyElem.value}`;
    const isValid = (val: string) => {
        if (val === "Refiner") {
            return true;
        }

        const m = `${val}`.match(/^Edit Stage (\d+)$/);
        if (!m) {
            return false;
        }

        const refId = parseInt(m[1], 10);
        return stageIds.includes(refId) && refId < stageId;
    };

    for (const opt of Array.from(applyElem.options)) {
        if (isValid(opt.value)) {
            opt.hidden = false;
            opt.disabled = false;
            continue;
        }

        if (opt.value === selectedVal) {
            opt.hidden = true;
            opt.disabled = true;
        } else {
            opt.remove();
        }
    }
};

export const validateApplyAfter = (
    prefix: string,
    stageIds: number[],
    stageId: number,
): void => {
    const applyElem = Utils.getSelectElement(`${prefix}applyafter`);
    if (!applyElem) {
        return;
    }

    applyElem.classList.remove("is-invalid");
    document.getElementById(`${prefix}applyafter_error`)?.remove();

    const val = `${applyElem.value}`;
    const applyInvalid =
        isMissingStageRef(val, stageIds) ||
        (/^Edit Stage \d+$/.test(val) &&
            parseInt(val.split(" ")[2], 10) >= stageId);

    if (!applyInvalid) {
        return;
    }

    applyElem.classList.add("is-invalid");

    const err = document.createElement("div");
    err.id = `${prefix}applyafter_error`;
    err.className = "text-danger";
    err.style.marginTop = "4px";
    err.innerText =
        "Invalid Apply After: Dependency chain has changed! Adjust the apply after stage.";

    findParentOfClass(applyElem, "auto-input").appendChild(err);
};
