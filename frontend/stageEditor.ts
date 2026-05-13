import { createGenerateWrap } from "./generateWrap";
import { createObservers, type ObserversApi } from "./observers";
import { applyEditorLayout, showStages } from "./renderStages";
import { isBase2EditGroupEnabled } from "./rootStage";
import {
    getStages,
    type StagesIOSaveDeps,
    saveStages,
    serializeStagesFromUi,
    updateStageFromUi,
} from "./stagesIO";
import type { Stage } from "./types";

export interface StageEditorApi {
    init: () => void;
    startGenerateWrapRetry: (intervalMs?: number) => void;
}

export function stageEditor(): StageEditorApi {
    let editor: HTMLElement | null = null;
    let observers!: ObserversApi;

    const createEditorElem = () => {
        let elem = document.getElementById("base2edit_stage_editor");
        if (!elem) {
            elem = document.createElement("div");
            elem.id = "base2edit_stage_editor";
            elem.className = "base2edit-stage-editor keep_group_visible";
            document
                .getElementById("input_group_content_baseedit")
                ?.appendChild(elem);
        }

        applyEditorLayout(elem);
        editor = elem;
    };

    const saveDeps: StagesIOSaveDeps = {
        getIsEnabled: isBase2EditGroupEnabled,
        onAfterSave: (json: string) => {
            observers.markPersisted(json, isBase2EditGroupEnabled());
            observers.publishStageAvailability();
        },
    };

    const saveStagesWired = (stages: Stage[]) => saveStages(stages, saveDeps);
    const serializeFromUi = () => serializeStagesFromUi(saveDeps);

    observers = createObservers({
        getStages,
        saveStages: saveStagesWired,
        updateStageFromUi,
    });

    const generateWrap = createGenerateWrap({
        getStages,
        serializeStagesFromUi: serializeFromUi,
    });

    const doShowStages = () => {
        if (!editor) return;
        showStages(editor, {
            getStages,
            saveStages: saveStagesWired,
            serializeStagesFromUi: serializeFromUi,
        });
    };

    const init = () => {
        createEditorElem();
        generateWrap.tryWrap();
        doShowStages();
        if (editor) {
            observers.installStageChangeListener(editor);
        }
        observers.startPublishedStageSync();
        observers.publishStageAvailability();
    };

    const startGenerateWrapRetry = (intervalMs?: number) => {
        if (intervalMs !== undefined) {
            generateWrap.startRetry(intervalMs);
        } else {
            generateWrap.startRetry();
        }
    };

    return {
        init,
        startGenerateWrapRetry,
    };
}
