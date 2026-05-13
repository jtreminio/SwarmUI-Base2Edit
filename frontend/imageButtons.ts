export interface ImageButtonsApi {
    init: (onRun: (src: string) => void) => boolean;
    waitFor: (onRun: (src: string) => void) => void;
}

export const createImageButtons = (): ImageButtonsApi => {
    const BUTTON_LABEL = "Base2Edit";
    const BUTTON_TITLE = "Runs an edit-only Base2Edit pass on this image";

    let wrapped = false;

    const isMediaSupported = (src: string): boolean => {
        return !isVideoExt(src) && !isAudioExt(src);
    };

    const init = (onRun: (src: string) => void): boolean => {
        if (wrapped) {
            return true;
        }
        if (typeof buttonsForImage !== "function") {
            return false;
        }
        const originalButtonsForImage = buttonsForImage;
        buttonsForImage = (fullsrc: string, src: string, metadata: unknown) => {
            const buttons = originalButtonsForImage(fullsrc, src, metadata);
            if (
                typeof window.base2editRunEditOnlyFromImage !== "function" ||
                !isMediaSupported(src)
            ) {
                return buttons;
            }
            buttons.push({
                label: BUTTON_LABEL,
                title: BUTTON_TITLE,
                onclick: () => onRun(src),
            });
            return buttons;
        };
        wrapped = true;
        return true;
    };

    const waitFor = (onRun: (src: string) => void): void => {
        const interval = setInterval(() => {
            if (!init(onRun)) {
                return;
            }
            clearInterval(interval);
        }, 100);
    };

    return { init, waitFor };
};
