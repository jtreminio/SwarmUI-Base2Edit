class Base2EditStageEditor
{
    genButtonWrapped = false;
    removeBtnListener = false;
    genWrapInterval = null;
    _changeListenerElem = null;
    _stageSyncTimers = new Map();

    _isBase2EditGroupEnabled()
    {
        const toggler = document.getElementById("input_group_content_baseedit_toggle");
        return !toggler || !!toggler.checked;
    }

    init()
    {
        this.editor = document.getElementById("base2edit_stage_editor");
        if (!this.editor) {
            this.editor = document.createElement("div");
            this.editor.id = "base2edit_stage_editor";
            this.editor.className = "base2edit-stage-editor keep_group_visible";
            document.getElementById("input_group_content_baseedit").appendChild(this.editor);
        }

        this._wrapGenerateWithValidation();
        this._showStages();
        this._installStageChangeListener();
    }

    startGenerateWrapRetry(intervalMs = 250)
    {
        if (this.genWrapInterval) {
            return;
        }

        const tryWrap = () => {
            try {
                this._wrapGenerateWithValidation();
                if (typeof mainGenHandler !== "undefined"
                    && mainGenHandler
                    && typeof mainGenHandler.doGenerate === "function"
                    && mainGenHandler.doGenerate.__base2editWrapped
                ) {
                    clearInterval(this.genWrapInterval);
                    this.genWrapInterval = null;
                }
            }
            catch { }
        };

        tryWrap();
        this.genWrapInterval = setInterval(tryWrap, intervalMs);
    }

    createStage(applyAfter)
    {
        return {
            applyAfter: applyAfter,
            keepPreEditImage: document.getElementById("input_keeppreeditimage").checked,
            control: document.getElementById("input_editcontrol").value,
            model: document.getElementById("input_editmodel").value,
            vae: document.getElementById("input_editvae").value,
            steps: document.getElementById("input_editsteps").value,
            cfgScale: document.getElementById("input_editcfgscale").value,
            sampler: document.getElementById("input_editsampler").value,
            scheduler: document.getElementById("input_editscheduler").value
        };
    }

    getStages()
    {
        try {
            const stages = document.getElementById("input_editstages");
            return JSON.parse(stages.value ?? "[]");
        }
        catch {
            return [];
        }
    }

    saveStages(newStages)
    {
        const stages = document.getElementById("input_editstages");
        stages.value = JSON.stringify(newStages);
        if (this._isBase2EditGroupEnabled()) {
            triggerChangeFor(stages);
        }
    }

    isMissingStageRef(applyAfter, stageIdSet)
    {
        const m = applyAfter.match(/^Edit Stage (\d+)$/);
        if (!m) {
            return false;
        }

        return !stageIdSet.has(parseInt(m[1]));
    }

    validateStages()
    {
        const stages = this.getStages();
        const errors = [];

        for (let i = 0; i < (stages).length; i++) {
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

    _wrapGenerateWithValidation()
    {
        if (this.genButtonWrapped) {
            return;
        }

        const original = mainGenHandler.doGenerate.bind(mainGenHandler);
        const stageEditor = this;
        mainGenHandler.doGenerate = function(...args) {
            if (!stageEditor._isBase2EditGroupEnabled()) {
                return original(...args);
            }

            stageEditor._serializeStagesFromUi();
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

    _installStageChangeListener()
    {
        if (!this.editor || this._changeListenerElem === this.editor) {
            return;
        }

        const handler = (e) => {
            try {
                let target = e?.target;
                if (!(target instanceof Element)) {
                    return;
                }

                const stageWrap = target.closest("[data-base2edit-stage-id]");
                if (!stageWrap) {
                    return;
                }

                const stageId = parseInt(stageWrap.dataset.base2editStageId);
                if (stageId < 1) {
                    return;
                }

                // Ignore clicks on the remove-stage button
                if (target.closest('button[data-base2edit-action="remove-stage"]')) {
                    return;
                }

                const isApplyAfter = !!target.closest(`#base2edit_stage_${stageId}_applyafter`);
                this._scheduleStageSyncFromUi(stageId, isApplyAfter);
            }
            catch { }
        };

        this.editor.addEventListener("input", handler, true);
        this.editor.addEventListener("change", handler, true);
        this._changeListenerElem = this.editor;
    }

    _scheduleStageSyncFromUi(stageId, validateApplyAfter = false)
    {
        const existing = this._stageSyncTimers.get(stageId);
        if (existing) {
            clearTimeout(existing);
        }

        const t = setTimeout(() => {
            try {
                this._syncSingleStageFromUi(stageId, validateApplyAfter);
            }
            catch { }
        }, 125);

        this._stageSyncTimers.set(stageId, t);
    }

    _syncSingleStageFromUi(stageId, validateApplyAfter)
    {
        const stages = this.getStages();
        const idx = stageId - 1;
        if (idx < 0 || idx >= stages.length) {
            return;
        }

        const prefix = `base2edit_stage_${stageId}_`;
        this._updateStageFromUi(prefix, stages[idx]);
        this.saveStages(stages);

        if (!validateApplyAfter) {
            return;
        }

        const stageIdSet = new Set([0, ...stages.map((_, i) => i + 1)]);
        const applyElem = document.getElementById(`${prefix}applyafter`);
        if (applyElem) {
            this._cleanApplyAfterOptions(applyElem, stageIdSet, stageId);
            this._validateApplyAfter(prefix, stageIdSet, stageId);
        }
    }

    _serializeStagesFromUi()
    {
        const stages = this.getStages();

        for (let i = 0; i < stages.length; i++) {
            const stageId = i + 1;
            const prefix = `base2edit_stage_${stageId}_`;
            this._updateStageFromUi(prefix, stages[i]);
        }

        this.saveStages(stages);
    }

    _showStages()
    {
        const stages = this.getStages();
        const stageIdSet = new Set([0, ...stages.map((_, idx) => idx + 1)]);
        const list = document.createElement("div");

        this.editor.innerHTML = "";
        this.editor.appendChild(list);
        this._addRemoveBtnListener(list);

        const rootStage = {
            model: document.getElementById("input_editmodel"),
            vae: document.getElementById("input_editvae"),
            sampler: document.getElementById("input_editsampler"),
            scheduler: document.getElementById("input_editscheduler"),
            control: document.getElementById("input_editcontrol"),
            steps: document.getElementById("input_editsteps"),
            cfg: document.getElementById("input_editcfgscale"),
        };

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
            const applyAfter = this._buildApplyAfterList(stageIdSet, stageId, stage.applyAfter);
            const parts = this._buildFieldsForStage(rootStage, stage, prefix, applyAfter.values);

            content.insertAdjacentHTML("beforeend", parts.map(p => p.html).join(""));
            for (const p of parts) {
                try { p.runnable(); } catch { }
            }

            const applyElem = document.getElementById(`${prefix}applyafter`);
            if (applyElem) {
                this._cleanApplyAfterOptions(applyElem, stageIdSet, stageId);
                this._validateApplyAfter(prefix, stageIdSet, stageId);
            }
        });

        const addBtn = document.createElement("button");
        addBtn.className = "basic-button";
        addBtn.innerText = "+ Add Edit Stage";
        addBtn.addEventListener("click", (e) => {
            e.preventDefault();
            this._serializeStagesFromUi();
            const current = this.getStages();
            const newStage = this.createStage(`Edit Stage ${current.length}`);
            this.saveStages([...current, newStage]);
            this._showStages();
        });
        this.editor.appendChild(addBtn);
    }

    _addRemoveBtnListener(list)
    {
        list.addEventListener("click", (e) => {
            const btn = e.target.closest('button[data-base2edit-action="remove-stage"]');
            if (!btn) {
                return;
            }

            e.preventDefault();
            e.stopPropagation();

            this._serializeStagesFromUi();
            const stageId = parseInt(btn.closest("[data-base2edit-stage-id]").dataset.base2editStageId);
            const stages = this.getStages();
            stages.splice(stageId - 1, 1);
            this.saveStages(stages);
            this._showStages();
        });
    }

    _buildApplyAfterList(stageIdSet, stageId, currentVal)
    {
        const values = ["Base", "Refiner"];
        const refs = [...stageIdSet]
            .filter(id => id < stageId)
            .sort((a, b) => a - b)
            .map(id => `Edit Stage ${id}`);
        values.push(...refs);

        // Preserve current selection if it isn't in the valid list, so we don't change selection automatically.
        if (currentVal && !values.includes(currentVal)) {
            values.unshift(currentVal);
        }

        return { values };
    };

    _cleanApplyAfterOptions(applyElem, stageIdSet, stageId)
    {
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
            return stageIdSet.has(refId) && refId < stageId;
        };

        // Remove invalid options so users can't select them.
        // If the currently selected value is invalid, keep it as a hidden placeholder
        // so we don't change selection automatically, but it won't appear in the dropdown list.
        for (const opt of [...applyElem.options]) {
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

    _buildFieldsForStage(rootStage, stage, prefix, applyValues)
    {
        const parts = [];

        parts.push(getHtmlForParam({
            id: "keeppreeditimage",
            name: "Keep Pre-Edit Image",
            description: "When enabled, saves the image immediately before this edit stage begins.",
            type: "boolean",
            default: stage.keepPreEditImage ? "true" : "false",
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
            view_min: rootStage.control.view_min,
            view_max: rootStage.control.view_max,
            view_type: "slider",
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
            view_min: rootStage.steps.view_min,
            view_max: rootStage.steps.view_max,
            view_type: "slider",
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editcfgscale",
            name: "Edit CFG Scale",
            description: "CFG Scale for this edit stage.",
            type: "decimal",
            default: `${stage.cfgScale}`,
            min: rootStage.cfg.min,
            max: rootStage.cfg.max,
            step: rootStage.cfg.step,
            view_min: rootStage.cfg.view_min,
            view_max: rootStage.cfg.view_max,
            view_type: "slider",
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editmodel",
            name: "Edit Model",
            description: "The model to use for this edit stage.",
            type: "model",
            subtype: "Stable-Diffusion",
            values: rootStage.model.values,
            default: stage.model,
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editvae",
            name: "Edit VAE",
            description: "VAE to use for this edit stage.",
            type: "model",
            subtype: "VAE",
            values: Array.from(rootStage.vae.options).map(o => o.value),
            value_names: Array.from(rootStage.vae.options).map(o => o.label),
            default: stage.vae,
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editsampler",
            name: "Edit Sampler",
            description: "Sampler to use for this edit stage.",
            type: "dropdown",
            values: Array.from(rootStage.sampler.options).map(o => o.value),
            value_names: Array.from(rootStage.sampler.options).map(o => o.label),
            default: stage.sampler,
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editscheduler",
            name: "Edit Scheduler",
            description: "Scheduler to use for this edit stage.",
            type: "dropdown",
            values: Array.from(rootStage.scheduler.options).map(o => o.value),
            value_names: Array.from(rootStage.scheduler.options).map(o => o.label),
            default: stage.scheduler,
            toggleable: false,
        }, prefix));

        return parts;
    }

    _validateApplyAfter(prefix, stageIdSet, stageId)
    {
        const applyElem = document.getElementById(`${prefix}applyafter`);
        if (!applyElem) {
            return;
        }

        applyElem.classList.remove("is-invalid");
        document.getElementById(`${prefix}applyafter_error`)?.remove();

        const val = `${applyElem.value}`;
        const applyInvalid = this.isMissingStageRef(val, stageIdSet)
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

    _updateStageFromUi(prefix, stage)
    {
        const val = (id, isBool = false) => {
            const el = document.getElementById(`${prefix}${id}`);
            if (!el) {
                return null;
            }

            if (isBool) {
                return !!el.checked;
            }

            return el.value;
        };

        stage.applyAfter = `${val("applyafter") || stage.applyAfter}`;
        stage.keepPreEditImage = !!val("keeppreeditimage", true);
        stage.control = parseFloat(val("editcontrol") || stage.control);
        stage.model = `${val("editmodel") || stage.model}`;
        stage.vae = `${val("editvae") || stage.vae}`;
        stage.steps = parseInt(val("editsteps") || stage.steps);
        stage.cfgScale = parseFloat(val("editcfgscale") || stage.cfgScale);
        stage.sampler = `${val("editsampler") || stage.sampler}`;
        stage.scheduler = `${val("editscheduler") || stage.scheduler}`;
    };
}

class Base2Edit
{
    base2editButtonLabel = "Base2Edit";
    base2editButtonTitle = "Runs an edit-only Base2Edit pass on this image";
    stageEditor = new Base2EditStageEditor();
    _imageButtonsWrapped = false;

    constructor()
    {
        this._registerEditPromptPrefix();
        window.base2editRunEditOnlyFromImage = this.runEditOnlyFromImage.bind(this);
        this._waitForButtons();

        if (!this._tryRegisterStageEditor()) {
            const interval = setInterval(() => {
                if (this._tryRegisterStageEditor()) {
                    clearInterval(interval);
                }
            }, 200);
        }

        this.stageEditor.startGenerateWrapRetry();
    }

    runEditOnlyFromImage(src)
    {
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

    _registerEditPromptPrefix()
    {
        promptTabComplete.registerPrefix(
            "edit",
            "Add a section of prompt text that is only used for Base2Edit edit stages.",
            () => [
                '\nUse "<edit>..." to apply to ALL Base2Edit edit stages (including LoRAs inside the section).',
                '\nUse "<edit[0]>..." to apply only to edit stage 0, "<edit[1]>..." for stage 1, etc.',
                '\nIf no "<edit>" / "<edit[0]>" section exists for a stage, Base2Edit falls back to the global prompt.'
            ],
            true,
        );
    }

    _isMediaSupported(src)
    {
        return !(typeof isVideoExt === "function" && isVideoExt(src))
            && !(typeof isAudioExt === "function" && isAudioExt(src));
    }

    _addButton(buttons, src)
    {
        if (!this._isMediaSupported(src)) {
            return;
        }

        buttons.push({
            label: this.base2editButtonLabel,
            title: this.base2editButtonTitle,
            onclick: () => this.runEditOnlyFromImage(src),
        });
    }

    _initImageButtons()
    {
        if (this._imageButtonsWrapped) {
            return true;
        }
        if (typeof buttonsForImage !== "function") {
            return false;
        }

        const originalButtonsForImage = buttonsForImage;
        const self = this;
        buttonsForImage = function(fullsrc, src, metadata) {
            const buttons = originalButtonsForImage(fullsrc, src, metadata);
            if (typeof window.base2editRunEditOnlyFromImage === "function") {
                self._addButton(buttons, src);
            }

            return buttons;
        };
        this._imageButtonsWrapped = true;

        return true;
    }

    _waitForButtons()
    {
        const checkInterval = setInterval(() => {
            if (!this._initImageButtons()) {
                return;
            }

            clearInterval(checkInterval);
        }, 100);
    }

    _tryRegisterStageEditor()
    {
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

new Base2Edit();
