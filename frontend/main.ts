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

const imageButtons = createImageButtons();
imageButtons.waitFor(runEditOnlyFromImage);

const tryRegisterStageEditor = (): boolean => {
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
