class Base2Edit
{
    private base2editButtonLabel = "Base2Edit";
    private base2editButtonTitle = "Runs an edit-only Base2Edit pass on this image";
    private stageEditor: StageEditor;
    private imageButtonsWrapped = false;

    public constructor()
    {
        this.stageEditor = new StageEditor();
        this.registerEditPromptPrefix();
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

new Base2Edit();
