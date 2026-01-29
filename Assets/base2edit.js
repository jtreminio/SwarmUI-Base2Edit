const Base2Edit = (() => {
    const base2editButtonLabel = 'Base2Edit';
    const base2editButtonTitle = 'Runs an edit-only Base2Edit pass on this image';

    const registerEditPromptPrefix = () => {
        promptTabComplete.registerPrefix(
            'edit',
            'Add a section of prompt text that is only used for the Edit stage.',
            () => [],
            true,
        );
    };

    const runEditOnlyFromImage = (src) => {
        if (!src) {
            showError('Cannot run Base2Edit: no image selected.');
            return;
        }

        const tmpImg = new Image();
        tmpImg.crossOrigin = 'Anonymous';
        tmpImg.onerror = () => showError('Cannot run Base2Edit: failed to load image.');
        tmpImg.onload = () => {
            const runWithUrl = (url) => {
                mainGenHandler.doGenerate({
                    initimage: url,
                    initimagecreativity: 0,
                    images: 1,
                    steps: 0,
                    aspectratio: 'Custom',
                    width: tmpImg.naturalWidth,
                    height: tmpImg.naturalHeight,
                    applyeditafter: 'Base',
                    refinermethod: null,
                    refinercontrolpercentage: null,
                    refinerupscale: null
                });
            };
            if (src.startsWith('data:')) {
                runWithUrl(src);
                return;
            }
            toDataURL(src, runWithUrl);
        };
        tmpImg.src = src;
    };

    const isMediaSupported = (src) => {
        return !(typeof isVideoExt === 'function' && isVideoExt(src))
            && !(typeof isAudioExt === 'function' && isAudioExt(src));
    };

    const addButton = (buttons, src) => {
        if (!isMediaSupported(src)) {
            return;
        }

        buttons.push({
            label: base2editButtonLabel,
            title: base2editButtonTitle,
            onclick: () => runEditOnlyFromImage(src),
        });
    };

    const wrapButtonsForImage = () => {
        const originalButtonsForImage = buttonsForImage;
        buttonsForImage = function(fullsrc, src, metadata) {
            const buttons = originalButtonsForImage(fullsrc, src, metadata);
            if (typeof window.base2editRunEditOnlyFromImage === 'function') {
                addButton(buttons, src);
            }

            return buttons;
        };
    };

    const initImageButtons = () => {
        if (typeof buttonsForImage !== 'function') {
            return false;
        }

        wrapButtonsForImage();
        return true;
    };

    const waitForButtons = () => {
        const checkInterval = setInterval(() => {
            if (!initImageButtons()) {
                return;
            }

            clearInterval(checkInterval);
        }, 100);
    };

    const init = () => {
        registerEditPromptPrefix();
        window.base2editRunEditOnlyFromImage = runEditOnlyFromImage;
        waitForButtons();
    };

    init();

    return Object.freeze({
        runEditOnlyFromImage
    });
})();
