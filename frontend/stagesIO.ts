import type { Stage } from "./types";
import { Utils } from "./Utils";

export type StagesIOSaveDeps = {
    getIsEnabled: () => boolean;
    onAfterSave: (json: string) => void;
};

export function getStages(): Stage[] {
    try {
        const stages = Utils.getInputElement("input_editstages");
        return JSON.parse(stages?.value ?? "[]");
    } catch {
        return [];
    }
}

export function saveStages(newStages: Stage[], deps: StagesIOSaveDeps): void {
    const stages = Utils.getInputElement(
        "input_editstages",
    ) as HTMLInputElement;
    stages.value = JSON.stringify(newStages);
    if (deps.getIsEnabled()) {
        triggerChangeFor(stages);
    }
    deps.onAfterSave(stages.value);
}

export function updateStageFromUi(prefix: string, stage: Stage): void {
    const val = (
        id: string,
        isBool = false,
    ): string | number | boolean | null => {
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

    stage.keepPreEditImage = !!val("keeppreeditimage", true);
    stage.refineOnly = !!val("editrefineonly", true);
    stage.applyAfter = `${val("applyafter") || stage.applyAfter}`;
    stage.control = parseFloat(String(val("editcontrol") ?? stage.control));
    stage.upscale = parseFloat(String(val("editupscale") ?? stage.upscale));
    stage.upscaleMethod = `${val("editupscalemethod") || stage.upscaleMethod}`;
    stage.model = `${val("editmodel") || stage.model}`;
    stage.vae = isEnabled("editvae") ? `${val("editvae") || stage.vae}` : null;
    stage.steps = parseInt(String(val("editsteps") || stage.steps), 10);
    stage.cfgScale = isEnabled("editcfgscale")
        ? parseFloat(String(val("editcfgscale") ?? stage.cfgScale))
        : null;
    stage.sampler = isEnabled("editsampler")
        ? `${val("editsampler") || stage.sampler}`
        : null;
    stage.scheduler = isEnabled("editscheduler")
        ? `${val("editscheduler") || stage.scheduler}`
        : null;
}

export function serializeStagesFromUi(deps: StagesIOSaveDeps): void {
    const stages = getStages();

    for (let i = 0; i < stages.length; i++) {
        const stageId = i + 1;
        const prefix = `base2edit_stage_${stageId}_`;
        updateStageFromUi(prefix, stages[i]);
    }

    saveStages(stages, deps);
}
