interface RootStage {
    applyAfter?: string;
    model: HTMLSelectElement;
    vae: HTMLSelectElement;
    sampler: HTMLSelectElement;
    scheduler: HTMLSelectElement;
    refineOnly: HTMLInputElement;
    control: HTMLInputElement;
    steps: HTMLInputElement;
    cfg: HTMLInputElement;
}

interface Stage {
    applyAfter: string;
    keepPreEditImage: boolean;
    refineOnly?: boolean;
    control: number;
    model: string;
    steps: number;
    vae: string | null;
    cfgScale: number | null;
    sampler: string | null;
    scheduler: string | null;
}

class StageEditor
{
    private editor: HTMLElement;
    private genButtonWrapped = false;
    private genWrapInterval: ReturnType<typeof setInterval> | null = null;
    private changeListenerElem: HTMLElement | null = null;
    private stageSyncTimers = new Map<number, ReturnType<typeof setTimeout>>();

    public init(): void
    {
        this.createEditor();
        this.wrapGenerateWithValidation();
        this.showStages();
        this.installStageChangeListener();
    }

    public startGenerateWrapRetry(intervalMs = 250): void
    {
        if (this.genWrapInterval) {
            return;
        }

        const tryWrap = () => {
            try {
                this.wrapGenerateWithValidation();
                if (typeof mainGenHandler !== "undefined"
                    && mainGenHandler
                    && typeof mainGenHandler.doGenerate === "function"
                    && mainGenHandler.doGenerate.__base2editWrapped
                ) {
                    clearInterval(this.genWrapInterval!);
                    this.genWrapInterval = null;
                }
            }
            catch { }
        };

        tryWrap();
        this.genWrapInterval = setInterval(tryWrap, intervalMs);
    }

    private createEditor(): void
    {
        let editor = document.getElementById("base2edit_stage_editor");
        if (!editor) {
            editor = document.createElement("div");
            editor.id = "base2edit_stage_editor";
            editor.className = "base2edit-stage-editor keep_group_visible";
            document.getElementById("input_group_content_baseedit")!.appendChild(editor);
        }

        this.editor = editor;
    }

    private getRootStage(): RootStage
    {
        return {
            model: Utils.getSelectElement("input_editmodel"),
            vae: Utils.getSelectElement("input_editvae"),
            sampler: Utils.getSelectElement("input_editsampler"),
            scheduler: Utils.getSelectElement("input_editscheduler"),
            refineOnly: Utils.getInputElement("input_refineonly"),
            control: Utils.getInputElement("input_editcontrol"),
            steps: Utils.getInputElement("input_editsteps"),
            cfg: Utils.getInputElement("input_editcfgscale"),
        };
    }

    private createStage(applyAfter: string): Stage
    {
        const readToggleableRoot = (id: string): string | null => {
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
            refineOnly: Utils.getInputElement("input_refineonly").checked,
            control: parseFloat(Utils.getInputElement("input_editcontrol").value),
            model: Utils.getInputElement("input_editmodel").value,
            steps: parseInt(Utils.getInputElement("input_editsteps").value),
            vae: readToggleableRoot("editvae"),
            cfgScale: parseFloat(readToggleableRoot("editcfgscale")),
            sampler: readToggleableRoot("editsampler"),
            scheduler: readToggleableRoot("editscheduler")
        };
    }

    private getStages(): Stage[]
    {
        try {
            const stages = Utils.getInputElement("input_editstages");
            return JSON.parse(stages?.value ?? "[]");
        }
        catch {
            return [];
        }
    }

    private saveStages(newStages: Stage[]): void
    {
        const stages = Utils.getInputElement("input_editstages");
        stages.value = JSON.stringify(newStages);
        if (this.isBase2EditGroupEnabled()) {
            triggerChangeFor(stages);
        }
    }

    private isMissingStageRef(applyAfter: string, stageIds: number[]): boolean
    {
        const m = applyAfter.match(/^Edit Stage (\d+)$/);
        if (!m) {
            return false;
        }

        return !stageIds.includes(parseInt(m[1]));
    }

    private validateStages(): string[]
    {
        const stages = this.getStages();
        const errors: string[] = [];

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

    private isBase2EditGroupEnabled(): boolean
    {
        const toggler = Utils.getInputElement("input_group_content_baseedit_toggle");
        return !toggler || !!toggler.checked;
    }

    private wrapGenerateWithValidation(): void
    {
        if (this.genButtonWrapped) {
            return;
        }

        const original = mainGenHandler.doGenerate.bind(mainGenHandler);
        const stageEditor = this;
        mainGenHandler.doGenerate = function(...args: unknown[]) {
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

    private installStageChangeListener(): void
    {
        if (this.changeListenerElem === this.editor) {
            return;
        }

        const handler = (e: Event) => {
            try {
                const target = e.target as Element;
                if (!target) {
                    return;
                }

                const stageWrap: HTMLElement | null = target.closest("[data-base2edit-stage-id]");
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

    private scheduleStageSyncFromUi(stageId: number, validateApplyAfter = false): void
    {
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

    private syncSingleStageFromUi(stageId: number, validateApplyAfter: boolean): void
    {
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

    private serializeStagesFromUi(): void
    {
        const stages = this.getStages();

        for (let i = 0; i < stages.length; i++) {
            const stageId = i + 1;
            const prefix = `base2edit_stage_${stageId}_`;
            this.updateStageFromUi(prefix, stages[i]);
        }

        this.saveStages(stages);
    }

    private showStages(): void
    {
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
                try { p.runnable(); } catch { }
            }

            const setToggle = (id: string, enabled: boolean) => {
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

    private addRemoveBtnListener(list: HTMLElement): void
    {
        list.addEventListener("click", (e) => {
            const btn = (e.target as Element).closest('button[data-base2edit-action="remove-stage"]');
            if (!btn) {
                return;
            }

            e.preventDefault();
            e.stopPropagation();

            this.serializeStagesFromUi();
            const stageId = parseInt((btn.closest("[data-base2edit-stage-id]") as HTMLElement).dataset.base2editStageId!);
            const stages = this.getStages();
            stages.splice(stageId - 1, 1);
            this.saveStages(stages);
            this.showStages();
        });
    }

    private buildApplyAfterList(stageIds: number[], stageId: number, currentVal: string): string[]
    {
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

    private cleanApplyAfterOptions(applyElem: HTMLSelectElement, stageIds: number[], stageId: number): void
    {
        const selectedVal = `${applyElem.value}`;
        const isValid = (val: string) => {
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

    private buildFieldsForStage(
        stage: Stage,
        prefix: string,
        applyValues: string[]
    ): Array<{ html: string; runnable: () => void }>
    {
        const rootStage = this.getRootStage();
        const parts: Array<{ html: string; runnable: () => void }> = [];

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
            id: "editrefineonly",
            name: "Refine Only",
            description: "When enabled, this stage skips ReferenceLatent and runs as a plain refinement pass.",
            type: "boolean",
            default: `${stage.refineOnly ?? rootStage.refineOnly.checked}`,
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
            values:  Array.from(rootStage.model.options).map((o) => o.value),
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
            values:  Array.from(rootStage.sampler.options).map((o) => o.value),
            value_names: Array.from(rootStage.sampler.options).map((o) => o.label),
            default: stage.sampler ?? rootStage.sampler.value,
            toggleable: true,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "editscheduler",
            name: "Edit Scheduler",
            description: "Scheduler to use for this edit stage.",
            type: "dropdown",
            values:  Array.from(rootStage.scheduler.options).map((o) => o.value),
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

    private validateApplyAfter(prefix: string, stageIds: number[], stageId: number): void
    {
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

    private updateStageFromUi(prefix: string, stage: Stage): void
    {
        const val = (id: string, isBool = false): string | number | boolean | null => {
            const el = Utils.getInputElement(`${prefix}${id}`);
            if (!el) {
                return null;
            }

            if (isBool) {
                return !!el.checked;
            }

            return el.value;
        };

        const isEnabled = (id: string) => {
            const t = Utils.getInputElement(`${prefix}${id}_toggle`);
            return !t || !!t.checked;
        };

        stage.applyAfter = `${val("applyafter") || stage.applyAfter}`;
        stage.keepPreEditImage = !!val("keeppreeditimage", true);
        stage.refineOnly = !!val("editrefineonly", true);
        stage.control = parseFloat(String(val("editcontrol") ?? stage.control));
        stage.model = `${val("editmodel") || stage.model}`;
        stage.steps = parseInt(String(val("editsteps") || stage.steps), 10);
        stage.vae = isEnabled("editvae") ? `${val("editvae") || stage.vae}` : null;
        stage.cfgScale = isEnabled("editcfgscale") ? parseFloat(String(val("editcfgscale") ?? stage.cfgScale)) : null;
        stage.sampler = isEnabled("editsampler") ? `${val("editsampler") || stage.sampler}` : null;
        stage.scheduler = isEnabled("editscheduler") ? `${val("editscheduler") || stage.scheduler}` : null;
    };
}
