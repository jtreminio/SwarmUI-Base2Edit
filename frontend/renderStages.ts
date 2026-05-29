import { createStage, getRootStage } from "./rootStage";
import type { Stage } from "./types";
import { utils } from "./utils";
import {
    buildApplyAfterList,
    cleanApplyAfterOptions,
    validateApplyAfter,
} from "./validation";

export type RenderDeps = {
    getStages: () => Stage[];
    saveStages: (stages: Stage[]) => void;
    serializeStagesFromUi: () => void;
};

export const applyFullWidthLayout = (elem: HTMLElement): void => {
    elem.style.width = "100%";
    elem.style.maxWidth = "100%";
    elem.style.minWidth = "0";
};

export const applyEditorLayout = (editor: HTMLElement): void => {
    applyFullWidthLayout(editor);
    editor.style.flex = "1 1 100%";
    editor.style.overflow = "visible";
};

export const buildFieldsForStage = (
    stage: Stage,
    prefix: string,
    applyValues: string[],
): Array<{ html: string; runnable: () => void }> => {
    const rootStage = getRootStage();
    const parts: Array<{ html: string; runnable: () => void }> = [];

    parts.push(
        getHtmlForParam(
            {
                id: "keeppreeditimage",
                name: "Keep Pre-Edit Image",
                description:
                    "When enabled, saves the image immediately before this edit stage begins.",
                type: "boolean",
                default: `${stage.keepPreEditImage}`,
                toggleable: false,
                view_type: "normal",
                feature_flag: null,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editrefineonly",
                name: "Refine Only",
                description:
                    "When enabled, this stage skips ReferenceLatent and runs as a plain refinement pass.",
                type: "boolean",
                default: `${stage.refineOnly ?? rootStage.refineOnly.checked}`,
                toggleable: false,
                view_type: "normal",
                feature_flag: null,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "applyafter",
                name: "Apply After",
                description: "",
                type: "dropdown",
                values: applyValues,
                default: applyValues.includes(stage.applyAfter)
                    ? stage.applyAfter
                    : applyValues[0],
                toggleable: false,
                view_type: "normal",
                feature_flag: null,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editcontrol",
                name: "Edit Control",
                description:
                    "Controls how much of the edit sampling is applied.",
                type: "decimal",
                default: `${stage.control}`,
                min: rootStage.control.min,
                max: rootStage.control.max,
                step: rootStage.control.step,
                view_min: rootStage.control.min,
                view_max: rootStage.control.max,
                view_type: "slider",
                toggleable: false,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editupscale",
                name: "Edit Upscale",
                description:
                    "Optional upscale of the image before this edit stage. 1 disables upscaling.",
                type: "decimal",
                default: `${stage.upscale ?? parseFloat(rootStage.upscale.value || "1")}`,
                min: rootStage.upscale.min,
                max: rootStage.upscale.max,
                step: rootStage.upscale.step,
                view_min: 0.25,
                view_max: 4,
                view_type: "slider",
                toggleable: false,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editupscalemethod",
                name: "Edit Upscale Method",
                description:
                    "How to upscale this edit stage image when Edit Upscale is enabled.",
                type: "dropdown",
                values: Array.from(rootStage.upscaleMethod.options).map(
                    (o) => o.value,
                ),
                value_names: Array.from(rootStage.upscaleMethod.options).map(
                    (o) => o.label,
                ),
                default: stage.upscaleMethod ?? rootStage.upscaleMethod.value,
                toggleable: false,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editmodel",
                name: "Edit Model",
                description: "The model to use for this edit stage.",
                type: "model",
                subtype: "Stable-Diffusion",
                values: Array.from(rootStage.model.options).map((o) => o.value),
                default: stage.model,
                toggleable: false,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editvae",
                name: "Edit VAE",
                description: "VAE to use for this edit stage.",
                type: "model",
                subtype: "VAE",
                values: Array.from(rootStage.vae.options).map((o) => o.value),
                value_names: Array.from(rootStage.vae.options).map(
                    (o) => o.label,
                ),
                default: stage.vae ?? rootStage.vae.value,
                toggleable: true,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editsteps",
                name: "Edit Steps",
                description: "Number of steps for this edit stage.",
                type: "integer",
                default: `${stage.steps}`,
                min: rootStage.steps.min,
                max: rootStage.steps.max,
                step: rootStage.steps.step,
                view_min: 1,
                view_max: 100,
                view_type: "slider",
                toggleable: false,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editcfgscale",
                name: "Edit CFG Scale",
                description: "CFG Scale for this edit stage.",
                type: "decimal",
                default: `${stage.cfgScale ?? rootStage.cfgScale.value}`,
                min: rootStage.cfgScale.min,
                max: rootStage.cfgScale.max,
                step: rootStage.cfgScale.step,
                view_min: 1,
                view_max: 20,
                view_type: "slider",
                toggleable: true,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editsampler",
                name: "Edit Sampler",
                description: "Sampler to use for this edit stage.",
                type: "dropdown",
                values: Array.from(rootStage.sampler.options).map(
                    (o) => o.value,
                ),
                value_names: Array.from(rootStage.sampler.options).map(
                    (o) => o.label,
                ),
                default: stage.sampler ?? rootStage.sampler.value,
                toggleable: true,
            },
            prefix,
        ),
    );
    parts.push(
        getHtmlForParam(
            {
                id: "editscheduler",
                name: "Edit Scheduler",
                description: "Scheduler to use for this edit stage.",
                type: "dropdown",
                values: Array.from(rootStage.scheduler.options).map(
                    (o) => o.value,
                ),
                value_names: Array.from(rootStage.scheduler.options).map(
                    (o) => o.label,
                ),
                default: stage.scheduler ?? rootStage.scheduler.value,
                toggleable: true,
            },
            prefix,
        ),
    );

    return parts;
};

export const showStages = (editor: HTMLElement, deps: RenderDeps): void => {
    const stages = deps.getStages();
    const stageIds = [0, ...stages.map((_, idx) => idx + 1)];
    const list = document.createElement("div");
    list.className = "base2edit-stage-list";
    applyFullWidthLayout(list);

    editor.innerHTML = "";
    editor.appendChild(list);
    addRemoveBtnListener(list, deps);
    addToggleStageBtnListener(list, deps);
    addSkipStageBtnListener(list, deps);

    stages.forEach((stage, idx) => {
        const stageId = idx + 1;
        const expanded = stage.expanded !== false;
        const skipped = stage.skipped === true;
        const wrap = document.createElement("div");
        wrap.className = "input-group base2edit-stage-wrap b2e-stage-card";
        wrap.classList.add(
            expanded ? "input-group-open" : "input-group-closed",
        );
        if (skipped) {
            wrap.classList.add("b2e-skipped");
        }
        wrap.id = `base2edit_stage_${stageId}`;
        wrap.dataset.base2editStageId = `${stageId}`;
        applyFullWidthLayout(wrap);

        const collapseGlyph = expanded ? "&#x2B9F;" : "&#x2B9E;";
        const collapseTitle = expanded ? "Minimize stage" : "Expand stage";
        const skipTitle = skipped ? "Re-enable stage" : "Skip stage";
        const skipVariant = skipped ? " b2e-btn-skip-active" : "";
        const header = document.createElement("span");
        header.className = "input-group-header input-group-noshrink";
        header.dataset.base2editAction = "toggle-stage";
        header.title = collapseTitle;
        header.innerHTML = `
            <span class="header-label-wrap">
                <span class="auto-symbol">${collapseGlyph}</span>
                <span class="header-label">Edit Stage ${stageId}</span>
                <span class="header-label-spacer"></span>
                <span class="b2e-stage-card-actions">
                    <button type="button" class="basic-button b2e-btn-tiny${skipVariant}" title="${skipTitle}" data-base2edit-action="skip-stage">&#x23ED;&#xFE0E;</button>
                    <button class="interrupt-button b2e-btn-tiny" title="Remove stage" data-base2edit-action="remove-stage" id="base2edit_remove_stage_${stageId}">&times;</button>
                </span>
            </span>`;
        wrap.appendChild(header);

        const content = document.createElement("div");
        content.className = "input-group-content base2edit-stage-content";
        applyFullWidthLayout(content);
        wrap.appendChild(content);

        list.appendChild(wrap);

        const prefix = `base2edit_stage_${stageId}_`;
        const applyAfter = buildApplyAfterList(
            stageIds,
            stageId,
            stage.applyAfter,
        );
        const parts = buildFieldsForStage(stage, prefix, applyAfter);

        content.insertAdjacentHTML(
            "beforeend",
            parts.map((p) => p.html).join(""),
        );
        for (const p of parts) {
            try {
                p.runnable();
            } catch {}
        }

        const setToggle = (id: string, enabled: boolean) => {
            const el = document.getElementById(`${prefix}${id}`);
            const t = utils.getInputElement(`${prefix}${id}_toggle`);
            if (!el || !t) {
                return;
            }
            t.checked = !!enabled;
            doToggleEnable(`${prefix}${id}`);
        };

        setToggle("editcfgscale", stage.cfgScale != null);
        setToggle(
            "editsampler",
            stage.sampler != null && `${stage.sampler}` !== "",
        );
        setToggle(
            "editscheduler",
            stage.scheduler != null && `${stage.scheduler}` !== "",
        );
        setToggle("editvae", stage.vae != null && `${stage.vae}` !== "");

        const applyElem = utils.getSelectElement(`${prefix}applyafter`);
        if (applyElem) {
            cleanApplyAfterOptions(applyElem, stageIds, stageId);
            validateApplyAfter(prefix, stageIds, stageId);
        }
    });

    const addBtn = document.createElement("button");
    addBtn.type = "button";
    addBtn.className = "b2e-add-btn b2e-add-btn-clip";
    addBtn.innerText = "+ Add Edit Stage";
    addBtn.addEventListener("click", (e) => {
        e.preventDefault();
        deps.serializeStagesFromUi();
        const current = deps.getStages();
        const newStage = createStage(`Edit Stage ${current.length}`);
        deps.saveStages([...current, newStage]);
        showStages(editor, deps);
    });
    editor.appendChild(addBtn);
};

export const addRemoveBtnListener = (
    list: HTMLElement,
    deps: RenderDeps,
): void => {
    list.addEventListener("click", (e) => {
        const btn = (e.target as Element).closest(
            'button[data-base2edit-action="remove-stage"]',
        );
        if (!btn) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();

        deps.serializeStagesFromUi();
        const stageEl = btn.closest(
            "[data-base2edit-stage-id]",
        ) as HTMLElement | null;
        const rawId = stageEl?.dataset.base2editStageId;
        if (!rawId) {
            return;
        }
        const stageId = parseInt(rawId, 10);
        const stages = deps.getStages();
        stages.splice(stageId - 1, 1);
        deps.saveStages(stages);
        showStages(list.parentElement as HTMLElement, deps);
    });
};

export const addToggleStageBtnListener = (
    list: HTMLElement,
    deps: RenderDeps,
): void => {
    list.addEventListener("click", (e) => {
        const target = e.target as Element;
        if (
            target.closest(
                '[data-base2edit-action="skip-stage"], [data-base2edit-action="remove-stage"]',
            )
        ) {
            return;
        }
        const zone = target.closest('[data-base2edit-action="toggle-stage"]');
        if (!zone) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();

        deps.serializeStagesFromUi();
        const stageEl = zone.closest(
            "[data-base2edit-stage-id]",
        ) as HTMLElement | null;
        const rawId = stageEl?.dataset.base2editStageId;
        if (!rawId) {
            return;
        }
        const stageId = parseInt(rawId, 10);
        const stages = deps.getStages();
        const stage = stages[stageId - 1];
        if (!stage) {
            return;
        }
        stage.expanded = stage.expanded === false;
        deps.saveStages(stages);
        showStages(list.parentElement as HTMLElement, deps);
    });
};

export const addSkipStageBtnListener = (
    list: HTMLElement,
    deps: RenderDeps,
): void => {
    list.addEventListener("click", (e) => {
        const btn = (e.target as Element).closest(
            '[data-base2edit-action="skip-stage"]',
        );
        if (!btn) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();

        deps.serializeStagesFromUi();
        const stageEl = btn.closest(
            "[data-base2edit-stage-id]",
        ) as HTMLElement | null;
        const rawId = stageEl?.dataset.base2editStageId;
        if (!rawId) {
            return;
        }
        const stageId = parseInt(rawId, 10);
        const stages = deps.getStages();
        const stage = stages[stageId - 1];
        if (!stage) {
            return;
        }
        stage.skipped = stage.skipped !== true;
        deps.saveStages(stages);
        showStages(list.parentElement as HTMLElement, deps);
    });
};
