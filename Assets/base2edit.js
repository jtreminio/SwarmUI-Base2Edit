promptTabComplete.registerPrefix('edit', 'Add a section of prompt text that is only used for the Edit stage.', (prefix) => {
    return [];
}, true);

const base2editButtonLabel = 'Base2Edit';
const base2editButtonTitle = 'Runs an edit-only Base2Edit pass on this image';

function base2editRunEditOnlyFromImage(src) {
    if (!src) {
        showError('Cannot run Base2Edit: no image selected.');
        return;
    }
    let tmpImg = new Image();
    tmpImg.crossOrigin = 'Anonymous';
    tmpImg.onerror = () => {
        showError('Cannot run Base2Edit: failed to load image.');
    };
    tmpImg.onload = () => {
        let width = tmpImg.naturalWidth;
        let height = tmpImg.naturalHeight;
        let runWithUrl = (url) => {
            let input_overrides = {
                'initimage': url,
                'initimagecreativity': 0,
                'images': 1,
                'steps': 0,
                'aspectratio': 'Custom',
                'width': width,
                'height': height,
                'applyeditafter': 'Base',
                'refinermethod': null,
                'refinercontrolpercentage': null,
                'refinerupscale': null
            };
            mainGenHandler.doGenerate(input_overrides);
        };
        if (src.startsWith('data:')) {
            runWithUrl(src);
            return;
        }
        toDataURL(src, runWithUrl);
    };
    tmpImg.src = src;
}

window.base2editRunEditOnlyFromImage = base2editRunEditOnlyFromImage;

function base2editIsMediaUnsupported(src) {
    let isVideo = typeof isVideoExt === 'function' && isVideoExt(src);
    let isAudio = typeof isAudioExt === 'function' && isAudioExt(src);
    return isVideo || isAudio;
}

function base2editAddButton(buttons, src) {
    if (base2editIsMediaUnsupported(src)) {
        return;
    }
    buttons.push({
        label: base2editButtonLabel,
        title: base2editButtonTitle,
        onclick: () => {
            base2editRunEditOnlyFromImage(src);
        }
    });
}

function base2editWrapButtonsForImage() {
    let originalButtonsForImage = buttonsForImage;
    buttonsForImage = function(fullsrc, src, metadata) {
        let buttons = originalButtonsForImage(fullsrc, src, metadata);
        if (typeof base2editRunEditOnlyFromImage === 'function') {
            base2editAddButton(buttons, src);
        }
        return buttons;
    };
}

function base2editEnsureDefaultButtonChoice() {
    if (typeof defaultButtonChoices === 'undefined') {
        return;
    }
    let choiceList = defaultButtonChoices.split(',').map(item => item.trim());
    if (!choiceList.includes(base2editButtonLabel)) {
        choiceList.splice(1, 0, base2editButtonLabel);
        defaultButtonChoices = choiceList.join(',');
    }
}

function base2editInitImageButtons() {
    if (typeof buttonsForImage !== 'function') {
        return false;
    }
    base2editWrapButtonsForImage();
    base2editEnsureDefaultButtonChoice();
    return true;
}

function base2editWaitForButtons() {
    let checkInterval = setInterval(() => {
        if (!base2editInitImageButtons()) {
            return;
        }
        clearInterval(checkInterval);
    }, 100);
}

base2editWaitForButtons();
