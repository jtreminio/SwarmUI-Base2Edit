/// <reference path="./StageEditor.ts" />

class Base2Edit
{
    private base2editButtonLabel = "Base2Edit";
    private base2editButtonTitle = "Runs an edit-only Base2Edit pass on this image";
    private stageEditor: StageEditor;
    private imageButtonsWrapped = false;

    public constructor(stageEditor: StageEditor)
    {
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

    private runEditOnlyFromImage(src: string): void
    {
        if (!src) {
            showError("Cannot run Base2Edit: no image selected.");
            return;
        }

        const tmpImg = new Image();
        tmpImg.crossOrigin = "Anonymous";
        tmpImg.onerror = () => showError("Cannot run Base2Edit: failed to load image.");
        tmpImg.onload = () => {
            const runWithUrl = (url: string) => {
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

    private registerEditPromptPrefix(): void
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

    private registerB2EPromptPrefix(): void
    {
        promptTabComplete.registerPrefix(
            "b2eprompt",
            "Use a Base2Edit prompt reference by stage: global, base, refiner, or edit stage number.",
            () => [
                '\nUse "<b2eprompt[global]>" to reuse the final global prompt.',
                '\nUse "<b2eprompt[base]>" / "<b2eprompt[refiner]>" to reuse that stage prompt (fallback to global if missing).',
                '\nUse "<b2eprompt[0]>", "<b2eprompt[1]>", etc. for edit stage index 0+ (0-indexed, fallback to global if undefined).'
            ],
            false,
        );

        promptTabComplete.registerPrefix(
            "b2eprompt[global]",
            'Base2Edit prompt reference: final global prompt text.',
            () => [
                '\nInserts "<b2eprompt[global]>"'
            ],
            true,
        );

        promptTabComplete.registerPrefix(
            "b2eprompt[base]",
            'Base2Edit prompt reference: base prompt text (fallback to global if missing).',
            () => [
                '\nInserts "<b2eprompt[base]>"'
            ],
            true,
        );

        promptTabComplete.registerPrefix(
            "b2eprompt[refiner]",
            'Base2Edit prompt reference: refiner prompt text (fallback to global if missing).',
            () => [
                '\nInserts "<b2eprompt[refiner]>"',
                '\nFor edit stages, use numeric index 0+ (example: "<b2eprompt[0]>").'
            ],
            true,
        );
    }

    private isMediaSupported(src: string): boolean
    {
        return !(typeof isVideoExt === "function" && isVideoExt(src))
            && !(typeof isAudioExt === "function" && isAudioExt(src));
    }

    private addButton(buttons: Array<{ label: string; title: string; onclick: () => void }>, src: string): void
    {
        if (!this.isMediaSupported(src)) {
            return;
        }

        buttons.push({
            label: this.base2editButtonLabel,
            title: this.base2editButtonTitle,
            onclick: () => this.runEditOnlyFromImage(src),
        });
    }

    private initImageButtons(): boolean
    {
        if (this.imageButtonsWrapped) {
            return true;
        }
        if (typeof buttonsForImage !== "function") {
            return false;
        }

        const originalButtonsForImage = buttonsForImage;
        const self = this;
        buttonsForImage = function(fullsrc: string, src: string, metadata: unknown) {
            const buttons = originalButtonsForImage(fullsrc, src, metadata);
            if (typeof window.base2editRunEditOnlyFromImage === "function") {
                self.addButton(buttons, src);
            }

            return buttons;
        };
        this.imageButtonsWrapped = true;

        return true;
    }

    private waitForButtons(): void
    {
        const checkInterval = setInterval(() => {
            if (!this.initImageButtons()) {
                return;
            }

            clearInterval(checkInterval);
        }, 100);
    }

    private tryRegisterStageEditor(): boolean
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

new Base2Edit(new StageEditor());
