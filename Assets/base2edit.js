"use strict";
class StageEditor {
    editor;
    genButtonWrapped = false;
    genWrapInterval = null;
    changeListenerElem = null;
    stageSyncTimers = new Map();
    init() {
        this.createEditor();
        this.wrapGenerateWithValidation();
        this.showStages();
        this.installStageChangeListener();
    }
    startGenerateWrapRetry(intervalMs = 250) {
        if (this.genWrapInterval) {
            return;
        }
        const tryWrap = () => {
            try {
                this.wrapGenerateWithValidation();
                if (typeof mainGenHandler !== "undefined"
                    && mainGenHandler
                    && typeof mainGenHandler.doGenerate === "function"
                    && mainGenHandler.doGenerate.__base2editWrapped) {
                    clearInterval(this.genWrapInterval);
                    this.genWrapInterval = null;
                }
            }
            catch { }
        };
        tryWrap();
        this.genWrapInterval = setInterval(tryWrap, intervalMs);
    }
    createEditor() {
        let editor = document.getElementById("base2edit_stage_editor");
        if (!editor) {
            editor = document.createElement("div");
            editor.id = "base2edit_stage_editor";
            editor.className = "base2edit-stage-editor keep_group_visible";
            document.getElementById("input_group_content_baseedit").appendChild(editor);
        }
        this.editor = editor;
    }
    getRootStage() {
        return {
            model: Utils.getSelectElement("input_editmodel"),
            vae: Utils.getSelectElement("input_editvae"),
            sampler: Utils.getSelectElement("input_editsampler"),
            scheduler: Utils.getSelectElement("input_editscheduler"),
            control: Utils.getInputElement("input_editcontrol"),
            steps: Utils.getInputElement("input_editsteps"),
            cfg: Utils.getInputElement("input_editcfgscale"),
        };
    }
    createStage(applyAfter) {
        const readToggleableRoot = (id) => {
            const el = Utils.getInputElement(`input_${id}`);
            if (!el) {
                return null;
            }
            const t = Utils.getInputElement(`input_${id}_toggle`);
            if (t && !t.checked) {
                return null;
            }
            return el.value;
        };
        return {
            applyAfter: applyAfter,
            keepPreEditImage: Utils.getInputElement("input_keeppreeditimage").checked,
            control: parseFloat(Utils.getInputElement("input_editcontrol").value),
            model: Utils.getInputElement("input_editmodel").value,
            steps: parseInt(Utils.getInputElement("input_editsteps").value),
            vae: readToggleableRoot("editvae"),
            cfgScale: parseFloat(readToggleableRoot("editcfgscale")),
            sampler: readToggleableRoot("editsampler"),
            scheduler: readToggleableRoot("editscheduler")
        };
    }
    getStages() {
        try {
            const stages = Utils.getInputElement("input_editstages");
            return JSON.parse(stages?.value ?? "[]");
        }
        catch {
            return [];
        }
    }
    saveStages(newStages) {
        const stages = Utils.getInputElement("input_editstages");
        stages.value = JSON.stringify(newStages);
        if (this.isBase2EditGroupEnabled()) {
            triggerChangeFor(stages);
        }
    }
    isMissingStageRef(applyAfter, stageIds) {
        const m = applyAfter.match(/^Edit Stage (\d+)$/);
        if (!m) {
            return false;
        }
        return !stageIds.includes(parseInt(m[1]));
    }
    validateStages() {
        const stages = this.getStages();
        const errors = [];
        for (let i = 0; i < stages.length; i++) {
            const stage = stages[i];
            const stageId = i + 1;
            const a = stage.applyAfter;
            const m = `${a}`.match(/^Edit Stage (\d+)$/);
            if (m) {
                const refId = parseInt(m[1]);
                if (refId >= stageId) {
                    errors.push(`Base2Edit: Edit Stage ${stageId} cannot Apply After "${a}" (must reference an earlier stage).`);
                }
            }
        }
        return errors;
    }
    isBase2EditGroupEnabled() {
        const toggler = Utils.getInputElement("input_group_content_baseedit_toggle");
        return !toggler || !!toggler.checked;
    }
    wrapGenerateWithValidation() {
        if (this.genButtonWrapped) {
            return;
        }
        const original = mainGenHandler.doGenerate.bind(mainGenHandler);
        const stageEditor = this;
        mainGenHandler.doGenerate = function (...args) {
            if (!stageEditor.isBase2EditGroupEnabled()) {
                return original(...args);
            }
            stageEditor.serializeStagesFromUi();
            const errs = stageEditor.validateStages();
            if (errs.length > 0) {
                showError(errs[0]);
                return;
            }
            return original(...args);
        };
        mainGenHandler.doGenerate.__base2editWrapped = true;
        this.genButtonWrapped = true;
    }
    installStageChangeListener() {
        if (this.changeListenerElem === this.editor) {
            return;
        }
        const handler = (e) => {
            try {
                const target = e.target;
                if (!target) {
                    return;
                }
                const stageWrap = target.closest("[data-base2edit-stage-id]");
                if (!stageWrap) {
                    return;
                }
                const stageId = parseInt(stageWrap.dataset.base2editStageId ?? "0");
                if (stageId < 1) {
                    return;
                }
                if (target.closest('button[data-base2edit-action="remove-stage"]')) {
                    return;
                }
                const isApplyAfter = !!target.closest(`#base2edit_stage_${stageId}_applyafter`);
                this.scheduleStageSyncFromUi(stageId, isApplyAfter);
            }
            catch { }
        };
        this.editor.addEventListener("input", handler, true);
        this.editor.addEventListener("change", handler, true);
        this.changeListenerElem = this.editor;
    }
    scheduleStageSyncFromUi(stageId, validateApplyAfter = false) {
        const existing = this.stageSyncTimers.get(stageId);
        if (existing) {
            clearTimeout(existing);
        }
        const t = setTimeout(() => {
            try {
                this.syncSingleStageFromUi(stageId, validateApplyAfter);
            }
            catch { }
        }, 125);
        this.stageSyncTimers.set(stageId, t);
    }
    syncSingleStageFromUi(stageId, validateApplyAfter) {
        const stages = this.getStages();
        const idx = stageId - 1;
        if (idx < 0 || idx >= stages.length) {
            return;
        }
        const prefix = `base2edit_stage_${stageId}_`;
        this.updateStageFromUi(prefix, stages[idx]);
        this.saveStages(stages);
        if (!validateApplyAfter) {
            return;
        }
        const stageIds = [0, ...stages.map((_, i) => i + 1)];
        const applyElem = Utils.getSelectElement(`${prefix}applyafter`);
        if (applyElem) {
            this.cleanApplyAfterOptions(applyElem, stageIds, stageId);
            this.validateApplyAfter(prefix, stageIds, stageId);
        }
    }
    serializeStagesFromUi() {
        const stages = this.getStages();
        for (let i = 0; i < stages.length; i++) {
            const stageId = i + 1;
            const prefix = `base2edit_stage_${stageId}_`;
            this.updateStageFromUi(prefix, stages[i]);
        }
        this.saveStages(stages);
    }
    showStages() {
        const stages = this.getStages();
        const stageIds = [0, ...stages.map((_, idx) => idx + 1)];
        const list = document.createElement("div");
        this.editor.innerHTML = "";
        this.editor.appendChild(list);
        this.addRemoveBtnListener(list);
        stages.forEach((stage, idx) => {
            const stageId = idx + 1;
            const wrap = document.createElement("div");
            wrap.className = "input-group input-group-open";
            wrap.classList.add("border", "rounded", "p-2", "mb-2");
            wrap.id = `base2edit_stage_${stageId}`;
            wrap.dataset.base2editStageId = `${stageId}`;
            const header = document.createElement("span");
            header.className = "input-group-header input-group-noshrink";
            header.innerHTML =
                `<span class="header-label-wrap">`
                    + `<span class="header-label">Edit Stage ${stageId}</span>`
                    + `<span class="header-label-spacer"></span>`
                    + `<button class="interrupt-button" title="Remove stage" data-base2edit-action="remove-stage" id="base2edit_remove_stage_${stageId}">×</button>`
                    + `</span>`;
            wrap.appendChild(header);
            const content = document.createElement("div");
            content.className = "input-group-content";
            wrap.appendChild(content);
            list.appendChild(wrap);
            const prefix = `base2edit_stage_${stageId}_`;
            const applyAfter = this.buildApplyAfterList(stageIds, stageId, stage.applyAfter);
            const parts = this.buildFieldsForStage(stage, prefix, applyAfter);
            content.insertAdjacentHTML("beforeend", parts.map(p => p.html).join(""));
            for (const p of parts) {
                try {
                    p.runnable();
                }
                catch { }
            }
            const setToggle = (id, enabled) => {
                const el = document.getElementById(`${prefix}${id}`);
                const t = Utils.getInputElement(`${prefix}${id}_toggle`);
                if (!el || !t) {
                    return;
                }
                t.checked = !!enabled;
                doToggleEnable(`${prefix}${id}`);
            };
            setToggle("editcfgscale", stage.cfgScale != null);
            setToggle("editsampler", stage.sampler != null && `${stage.sampler}` !== "");
            setToggle("editscheduler", stage.scheduler != null && `${stage.scheduler}` !== "");
            setToggle("editvae", stage.vae != null && `${stage.vae}` !== "");
            const applyElem = Utils.getSelectElement(`${prefix}applyafter`);
            if (applyElem) {
                this.cleanApplyAfterOptions(applyElem, stageIds, stageId);
                this.validateApplyAfter(prefix, stageIds, stageId);
            }
        });
        const addBtn = document.createElement("button");
        addBtn.className = "basic-button";
        addBtn.innerText = "+ Add Edit Stage";
        addBtn.addEventListener("click", (e) => {
            e.preventDefault();
            this.serializeStagesFromUi();
            const current = this.getStages();
            const newStage = this.createStage(`Edit Stage ${current.length}`);
            this.saveStages([...current, newStage]);
            this.showStages();
        });
        this.editor.appendChild(addBtn);
    }
    addRemoveBtnListener(list) {
        list.addEventListener("click", (e) => {
            const btn = e.target.closest('button[data-base2edit-action="remove-stage"]');
            if (!btn) {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
            this.serializeStagesFromUi();
            const stageId = parseInt(btn.closest("[data-base2edit-stage-id]").dataset.base2editStageId);
            const stages = this.getStages();
            stages.splice(stageId - 1, 1);
            this.saveStages(stages);
            this.showStages();
        });
    }
    buildApplyAfterList(stageIds, stageId, currentVal) {
        const values = ["Base", "Refiner"];
        const refs = [...stageIds]
            .filter(id => id < stageId)
            .sort((a, b) => a - b)
            .map(id => `Edit Stage ${id}`);
        values.push(...refs);
        if (currentVal && !values.includes(currentVal)) {
            values.unshift(currentVal);
        }
        return values;
    }
    cleanApplyAfterOptions(applyElem, stageIds, stageId) {
        const selectedVal = `${applyElem.value}`;
        const isValid = (val) => {
            if (val === "Base" || val === "Refiner") {
                return true;
            }
            const m = `${val}`.match(/^Edit Stage (\d+)$/);
            if (!m) {
                return false;
            }
            const refId = parseInt(m[1]);
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
            }
            else {
                opt.remove();
            }
        }
    }
    buildFieldsForStage(stage, prefix, applyValues) {
        const rootStage = this.getRootStage();
        const parts = [];
        parts.push(getHtmlForParam({
            id: "keeppreeditimage",
            name: "Keep Pre-Edit Image",
            description: "When enabled, saves the image immediately before this edit stage begins.",
            type: "boolean",
            default: `${stage.keepPreEditImage}`,
            toggleable: false,
            view_type: "normal",
            feature_flag: null,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "applyafter",
            name: "Apply After",
            description: "",
            type: "dropdown",
            values: applyValues,
            default: applyValues.includes(stage.applyAfter) ? stage.applyAfter : applyValues[0],
            toggleable: false,
            view_type: "normal",
            feature_flag: null,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editcontrol",
            name: "Edit Control",
            description: "Controls how much of the edit sampling is applied.",
            type: "decimal",
            default: `${stage.control}`,
            min: rootStage.control.min,
            max: rootStage.control.max,
            step: rootStage.control.step,
            view_min: rootStage.control.min,
            view_max: rootStage.control.max,
            view_type: "slider",
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editmodel",
            name: "Edit Model",
            description: "The model to use for this edit stage.",
            type: "model",
            subtype: "Stable-Diffusion",
            values: Array.from(rootStage.model.options).map((o) => o.value),
            default: stage.model,
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editsteps",
            name: "Edit Steps",
            description: "Number of steps for this edit stage.",
            type: "integer",
            default: `${stage.steps}`,
            min: rootStage.steps.min,
            max: rootStage.steps.max,
            step: rootStage.steps.step,
            view_min: rootStage.steps.min,
            view_max: rootStage.steps.max,
            view_type: "slider",
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editcfgscale",
            name: "Edit CFG Scale",
            description: "CFG Scale for this edit stage.",
            type: "decimal",
            default: `${stage.cfgScale ?? rootStage.cfg.value}`,
            min: rootStage.cfg.min,
            max: rootStage.cfg.max,
            step: rootStage.cfg.step,
            view_min: rootStage.cfg.min,
            view_max: rootStage.cfg.max,
            view_type: "slider",
            toggleable: true,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editsampler",
            name: "Edit Sampler",
            description: "Sampler to use for this edit stage.",
            type: "dropdown",
            values: Array.from(rootStage.sampler.options).map((o) => o.value),
            value_names: Array.from(rootStage.sampler.options).map((o) => o.label),
            default: stage.sampler ?? rootStage.sampler.value,
            toggleable: true,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editscheduler",
            name: "Edit Scheduler",
            description: "Scheduler to use for this edit stage.",
            type: "dropdown",
            values: Array.from(rootStage.scheduler.options).map((o) => o.value),
            value_names: Array.from(rootStage.scheduler.options).map((o) => o.label),
            default: stage.scheduler ?? rootStage.scheduler.value,
            toggleable: true,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editvae",
            name: "Edit VAE",
            description: "VAE to use for this edit stage.",
            type: "model",
            subtype: "VAE",
            values: Array.from(rootStage.vae.options).map((o) => o.value),
            value_names: Array.from(rootStage.vae.options).map((o) => o.label),
            default: stage.vae ?? rootStage.vae.value,
            toggleable: true,
        }, prefix));
        return parts;
    }
    validateApplyAfter(prefix, stageIds, stageId) {
        const applyElem = Utils.getSelectElement(`${prefix}applyafter`);
        if (!applyElem) {
            return;
        }
        applyElem.classList.remove("is-invalid");
        document.getElementById(`${prefix}applyafter_error`)?.remove();
        const val = `${applyElem.value}`;
        const applyInvalid = this.isMissingStageRef(val, stageIds)
            || (/^Edit Stage \d+$/.test(val) && parseInt(val.split(" ")[2]) >= stageId);
        if (!applyInvalid) {
            return;
        }
        applyElem.classList.add("is-invalid");
        const err = document.createElement("div");
        err.id = `${prefix}applyafter_error`;
        err.className = "text-danger";
        err.style.marginTop = "4px";
        err.innerText = "Invalid Apply After: Dependency chain has changed! Adjust the apply after stage.";
        findParentOfClass(applyElem, "auto-input").appendChild(err);
    }
    updateStageFromUi(prefix, stage) {
        const val = (id, isBool = false) => {
            const el = Utils.getInputElement(`${prefix}${id}`);
            if (!el) {
                return null;
            }
            if (isBool) {
                return !!el.checked;
            }
            return el.value;
        };
        const isEnabled = (id) => {
            const t = Utils.getInputElement(`${prefix}${id}_toggle`);
            return !t || !!t.checked;
        };
        stage.applyAfter = `${val("applyafter") || stage.applyAfter}`;
        stage.keepPreEditImage = !!val("keeppreeditimage", true);
        stage.control = parseFloat(String(val("editcontrol") ?? stage.control));
        stage.model = `${val("editmodel") || stage.model}`;
        stage.steps = parseInt(String(val("editsteps") || stage.steps), 10);
        stage.vae = isEnabled("editvae") ? `${val("editvae") || stage.vae}` : null;
        stage.cfgScale = isEnabled("editcfgscale") ? parseFloat(String(val("editcfgscale") ?? stage.cfgScale)) : null;
        stage.sampler = isEnabled("editsampler") ? `${val("editsampler") || stage.sampler}` : null;
        stage.scheduler = isEnabled("editscheduler") ? `${val("editscheduler") || stage.scheduler}` : null;
    }
    ;
}
/// <reference path="./StageEditor.ts" />
class Base2Edit {
    base2editButtonLabel = "Base2Edit";
    base2editButtonTitle = "Runs an edit-only Base2Edit pass on this image";
    stageEditor;
    imageButtonsWrapped = false;
    constructor(stageEditor) {
        this.stageEditor = stageEditor;
        this.registerEditPromptPrefix();
        this.registerB2EPromptPrefix();
        window.base2editRunEditOnlyFromImage = this.runEditOnlyFromImage.bind(this);
        this.waitForButtons();
        if (!this.tryRegisterStageEditor()) {
            const interval = setInterval(() => {
                if (this.tryRegisterStageEditor()) {
                    clearInterval(interval);
                }
            }, 200);
        }
        this.stageEditor.startGenerateWrapRetry();
    }
    runEditOnlyFromImage(src) {
        if (!src) {
            showError("Cannot run Base2Edit: no image selected.");
            return;
        }
        const tmpImg = new Image();
        tmpImg.crossOrigin = "Anonymous";
        tmpImg.onerror = () => showError("Cannot run Base2Edit: failed to load image.");
        tmpImg.onload = () => {
            const runWithUrl = (url) => {
                mainGenHandler.doGenerate({
                    initimage: url,
                    initimagecreativity: 0,
                    images: 1,
                    steps: 0,
                    aspectratio: "Custom",
                    width: tmpImg.naturalWidth,
                    height: tmpImg.naturalHeight,
                    applyeditafter: "Base",
                    refinermethod: null,
                    refinercontrolpercentage: null,
                    refinerupscale: null
                });
            };
            if (src.startsWith("data:")) {
                runWithUrl(src);
                return;
            }
            toDataURL(src, runWithUrl);
        };
        tmpImg.src = src;
    }
    registerEditPromptPrefix() {
        promptTabComplete.registerPrefix("edit", "Add a section of prompt text that is only used for Base2Edit edit stages.", () => [
            '\nUse "<edit>..." to apply to ALL Base2Edit edit stages (including LoRAs inside the section).',
            '\nUse "<edit[0]>..." to apply only to edit stage 0, "<edit[1]>..." for stage 1, etc.',
            '\nIf no "<edit>" / "<edit[0]>" section exists for a stage, Base2Edit falls back to the global prompt.'
        ], true);
    }
    registerB2EPromptPrefix() {
        promptTabComplete.registerPrefix("b2eprompt", "Use a Base2Edit prompt reference by stage: global, base, refiner, or edit stage number.", () => [
            '\nUse "<b2eprompt[global]>" to reuse the final global prompt.',
            '\nUse "<b2eprompt[base]>" / "<b2eprompt[refiner]>" to reuse that stage prompt (fallback to global if missing).',
            '\nUse "<b2eprompt[0]>", "<b2eprompt[1]>", etc. for edit stage index 0+ (0-indexed, fallback to global if undefined).'
        ], false);
        promptTabComplete.registerPrefix("b2eprompt[global]", 'Base2Edit prompt reference: final global prompt text.', () => [
            '\nInserts "<b2eprompt[global]>"'
        ], true);
        promptTabComplete.registerPrefix("b2eprompt[base]", 'Base2Edit prompt reference: base prompt text (fallback to global if missing).', () => [
            '\nInserts "<b2eprompt[base]>"'
        ], true);
        promptTabComplete.registerPrefix("b2eprompt[refiner]", 'Base2Edit prompt reference: refiner prompt text (fallback to global if missing).', () => [
            '\nInserts "<b2eprompt[refiner]>"',
            '\nFor edit stages, use numeric index 0+ (example: "<b2eprompt[0]>").'
        ], true);
    }
    isMediaSupported(src) {
        return !(typeof isVideoExt === "function" && isVideoExt(src))
            && !(typeof isAudioExt === "function" && isAudioExt(src));
    }
    addButton(buttons, src) {
        if (!this.isMediaSupported(src)) {
            return;
        }
        buttons.push({
            label: this.base2editButtonLabel,
            title: this.base2editButtonTitle,
            onclick: () => this.runEditOnlyFromImage(src),
        });
    }
    initImageButtons() {
        if (this.imageButtonsWrapped) {
            return true;
        }
        if (typeof buttonsForImage !== "function") {
            return false;
        }
        const originalButtonsForImage = buttonsForImage;
        const self = this;
        buttonsForImage = function (fullsrc, src, metadata) {
            const buttons = originalButtonsForImage(fullsrc, src, metadata);
            if (typeof window.base2editRunEditOnlyFromImage === "function") {
                self.addButton(buttons, src);
            }
            return buttons;
        };
        this.imageButtonsWrapped = true;
        return true;
    }
    waitForButtons() {
        const checkInterval = setInterval(() => {
            if (!this.initImageButtons()) {
                return;
            }
            clearInterval(checkInterval);
        }, 100);
    }
    tryRegisterStageEditor() {
        if (typeof postParamBuildSteps === "undefined" || !Array.isArray(postParamBuildSteps)) {
            return false;
        }
        postParamBuildSteps.push(() => {
            try {
                this.stageEditor.init();
            }
            catch (e) {
                console.log("Base2Edit: failed to build stage editor", e);
            }
        });
        return true;
    }
}
new Base2Edit(new StageEditor());
const Utils = {
    getInputElement: (id) => {
        return document.getElementById(id);
    },
    getSelectElement: (id) => {
        return document.getElementById(id);
    },
    getButtonElement: (id) => {
        return document.getElementById(id);
    },
};
//# sourceMappingURL=base2edit.js.map