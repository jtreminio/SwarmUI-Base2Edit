import type { RootStage, Stage } from "./types";
import { Utils } from "./Utils";

export function getRootStage(): RootStage {
    return {
        refineOnly: Utils.getInputElement(
            "input_refineonly",
        ) as HTMLInputElement,
        control: Utils.getInputElement("input_editcontrol") as HTMLInputElement,
        upscale: Utils.getInputElement("input_editupscale") as HTMLInputElement,
        upscaleMethod: Utils.getSelectElement(
            "input_editupscalemethod",
        ) as HTMLSelectElement,
        model: Utils.getSelectElement("input_editmodel") as HTMLSelectElement,
        vae: Utils.getSelectElement("input_editvae") as HTMLSelectElement,
        steps: Utils.getInputElement("input_editsteps") as HTMLInputElement,
        cfgScale: Utils.getInputElement(
            "input_editcfgscale",
        ) as HTMLInputElement,
        sampler: Utils.getSelectElement(
            "input_editsampler",
        ) as HTMLSelectElement,
        scheduler: Utils.getSelectElement(
            "input_editscheduler",
        ) as HTMLSelectElement,
    };
}

export function createStage(applyAfter: string): Stage {
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
        keepPreEditImage: (
            Utils.getInputElement("input_keeppreeditimage") as HTMLInputElement
        ).checked,
        refineOnly: (
            Utils.getInputElement("input_refineonly") as HTMLInputElement
        ).checked,
        applyAfter: applyAfter,
        control: parseFloat(
            (Utils.getInputElement("input_editcontrol") as HTMLInputElement)
                .value,
        ),
        upscale: parseFloat(
            (Utils.getInputElement("input_editupscale") as HTMLInputElement)
                .value,
        ),
        upscaleMethod: (
            Utils.getInputElement("input_editupscalemethod") as HTMLInputElement
        ).value,
        model: (Utils.getInputElement("input_editmodel") as HTMLInputElement)
            .value,
        vae: readToggleableRoot("editvae"),
        steps: parseInt(
            (Utils.getInputElement("input_editsteps") as HTMLInputElement)
                .value,
            10,
        ),
        cfgScale: parseFloat(readToggleableRoot("editcfgscale") ?? ""),
        sampler: readToggleableRoot("editsampler"),
        scheduler: readToggleableRoot("editscheduler"),
    };
}

export function isBase2EditGroupEnabled(): boolean {
    const toggler = Utils.getInputElement(
        "input_group_content_baseedit_toggle",
    );
    return !toggler || !!toggler.checked;
}
