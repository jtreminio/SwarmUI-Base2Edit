import type { RootStage, Stage } from "./types";
import { utils } from "./utils";

export const getRootStage = (): RootStage => {
    return {
        refineOnly: utils.getInputElement(
            "input_refineonly",
        ) as HTMLInputElement,
        control: utils.getInputElement("input_editcontrol") as HTMLInputElement,
        upscale: utils.getInputElement("input_editupscale") as HTMLInputElement,
        upscaleMethod: utils.getSelectElement(
            "input_editupscalemethod",
        ) as HTMLSelectElement,
        model: utils.getSelectElement("input_editmodel") as HTMLSelectElement,
        vae: utils.getSelectElement("input_editvae") as HTMLSelectElement,
        steps: utils.getInputElement("input_editsteps") as HTMLInputElement,
        cfgScale: utils.getInputElement(
            "input_editcfgscale",
        ) as HTMLInputElement,
        sampler: utils.getSelectElement(
            "input_editsampler",
        ) as HTMLSelectElement,
        scheduler: utils.getSelectElement(
            "input_editscheduler",
        ) as HTMLSelectElement,
    };
};

export const createStage = (applyAfter: string): Stage => {
    const readToggleableRoot = (id: string): string | null => {
        const el = utils.getInputElement(`input_${id}`);
        if (!el) {
            return null;
        }

        const t = utils.getInputElement(`input_${id}_toggle`);
        if (t && !t.checked) {
            return null;
        }

        return el.value;
    };

    return {
        keepPreEditImage: (
            utils.getInputElement("input_keeppreeditimage") as HTMLInputElement
        ).checked,
        refineOnly: (
            utils.getInputElement("input_refineonly") as HTMLInputElement
        ).checked,
        applyAfter: applyAfter,
        control: parseFloat(
            (utils.getInputElement("input_editcontrol") as HTMLInputElement)
                .value,
        ),
        upscale: parseFloat(
            (utils.getInputElement("input_editupscale") as HTMLInputElement)
                .value,
        ),
        upscaleMethod: (
            utils.getInputElement("input_editupscalemethod") as HTMLInputElement
        ).value,
        model: (utils.getInputElement("input_editmodel") as HTMLInputElement)
            .value,
        vae: readToggleableRoot("editvae"),
        steps: parseInt(
            (utils.getInputElement("input_editsteps") as HTMLInputElement)
                .value,
            10,
        ),
        cfgScale: parseFloat(readToggleableRoot("editcfgscale") ?? ""),
        sampler: readToggleableRoot("editsampler"),
        scheduler: readToggleableRoot("editscheduler"),
    };
};

export const isBase2EditGroupEnabled = (): boolean => {
    const toggler = utils.getInputElement(
        "input_group_content_baseedit_toggle",
    );
    return !toggler || !!toggler.checked;
};
