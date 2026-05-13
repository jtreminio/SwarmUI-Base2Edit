/**
 * Lightweight DOM builders for Base2Edit frontend tests.
 *
 * Every input the production code reads is keyed by an HTML id and looked up
 * via `Utils.getInputElement` / `Utils.getSelectElement`, both of which call
 * `document.getElementById`. These helpers create elements with the requested
 * id, append them to `document.body`, and return the element for further
 * customization. `scripts/jest.setupAfterEnv.js` resets `document.body.innerHTML`
 * after every test, so no manual cleanup is required.
 */

export const addInput = (
    id: string,
    value = "",
    type = "text",
): HTMLInputElement => {
    const el = document.createElement("input");
    el.id = id;
    el.type = type;
    el.value = value;
    document.body.appendChild(el);
    return el;
};

export const addCheckbox = (id: string, checked = true): HTMLInputElement => {
    const el = document.createElement("input");
    el.id = id;
    el.type = "checkbox";
    el.checked = checked;
    document.body.appendChild(el);
    return el;
};

export const addToggle = (baseId: string, checked = true): HTMLInputElement => {
    return addCheckbox(`${baseId}_toggle`, checked);
};

export const addSelect = (
    id: string,
    value = "",
    optionValues: string[] = [],
): HTMLSelectElement => {
    const el = document.createElement("select");
    el.id = id;
    const values =
        optionValues.length > 0 ? optionValues : value ? [value] : [];
    for (const v of values) {
        const opt = document.createElement("option");
        opt.value = v;
        opt.text = v;
        el.appendChild(opt);
    }
    if (value && !values.includes(value)) {
        const opt = document.createElement("option");
        opt.value = value;
        opt.text = value;
        el.appendChild(opt);
    }
    el.value = value;
    document.body.appendChild(el);
    return el;
};
