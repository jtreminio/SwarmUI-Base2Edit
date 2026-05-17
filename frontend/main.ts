import { openFramePickerModal } from "./framePicker/modal";
import { createImageButtons } from "./imageButtons";
import {
    registerB2EImagePrefix,
    registerB2EPromptPrefix,
    registerEditPromptPrefix,
} from "./promptPrefixes";
import { runEditOnlyFromImage } from "./runEditOnly";
import { stageEditor } from "./stageEditor";

const editor = stageEditor();

window.base2editRunEditOnlyFromImage = runEditOnlyFromImage;

registerEditPromptPrefix();
registerB2EPromptPrefix();
registerB2EImagePrefix();

createImageButtons().waitFor(runEditOnlyFromImage);

export const tryRegisterStageEditor = (): boolean => {
    if (
        typeof postParamBuildSteps === "undefined" ||
        !Array.isArray(postParamBuildSteps)
    ) {
        return false;
    }
    postParamBuildSteps.push(() => {
        try {
            editor.init();
        } catch (e) {
            console.log("Base2Edit: failed to build stage editor", e);
        }
    });
    return true;
};

if (!tryRegisterStageEditor()) {
    const interval = setInterval(() => {
        if (tryRegisterStageEditor()) {
            clearInterval(interval);
        }
    }, 200);
}

editor.startGenerateWrapRetry();

registerMediaButton(
    "Pick Frames",
    (src) => openFramePickerModal(src),
    "Open the Frame Picker to extract and save individual frames from this video",
    ["video"],
    false,
    true,
);
