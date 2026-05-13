import { isBase2EditGroupEnabled } from "./rootStage";
import type { Stage } from "./types";
import { Utils } from "./Utils";
import { cleanApplyAfterOptions, validateApplyAfter } from "./validation";

export type ObserversDeps = {
    getStages: () => Stage[];
    saveStages: (stages: Stage[]) => void;
    updateStageFromUi: (prefix: string, stage: Stage) => void;
};

export type ObserversApi = {
    startPublishedStageSync: () => void;
    installStageChangeListener: (editor: HTMLElement) => void;
    publishStageAvailability: () => void;
    markPersisted: (json: string, enabled: boolean) => void;
};

export const createObservers = (deps: ObserversDeps): ObserversApi => {
    const stageSyncTimers = new Map<number, ReturnType<typeof setTimeout>>();
    let stagesInputSyncInterval: ReturnType<typeof setInterval> | null = null;
    let lastKnownStagesJson = "";
    let lastKnownBase2EditEnabled = false;
    let changeListenerElem: HTMLElement | null = null;

    const buildStageSnapshot = (): Base2EditStageSnapshot => {
        const enabled = isBase2EditGroupEnabled();
        const stageCount = enabled ? deps.getStages().length + 1 : 0;
        const refs: string[] = [];
        for (let i = 0; i < stageCount; i++) {
            refs.push(`edit${i}`);
        }
        return {
            enabled,
            stageCount,
            refs,
        };
    };

    const cloneStageSnapshot = (
        snapshot: Base2EditStageSnapshot,
    ): Base2EditStageSnapshot => {
        return {
            enabled: snapshot.enabled,
            stageCount: snapshot.stageCount,
            refs: [...snapshot.refs],
        };
    };

    const ensureStageRegistry = () => {
        const getSnapshot = () => cloneStageSnapshot(buildStageSnapshot());
        if (!window.base2editStageRegistry) {
            window.base2editStageRegistry = { getSnapshot };
            return;
        }

        window.base2editStageRegistry.getSnapshot = getSnapshot;
    };

    const publishStageAvailability = () => {
        ensureStageRegistry();
        const snapshot = cloneStageSnapshot(buildStageSnapshot());
        document.dispatchEvent(
            new CustomEvent("base2edit:stages-changed", {
                detail: snapshot,
            }),
        );
    };

    const startPublishedStageSync = () => {
        if (stagesInputSyncInterval) {
            return;
        }

        lastKnownStagesJson =
            Utils.getInputElement("input_editstages")?.value ?? "";
        lastKnownBase2EditEnabled = isBase2EditGroupEnabled();
        stagesInputSyncInterval = setInterval(() => {
            const currentStagesJson =
                Utils.getInputElement("input_editstages")?.value ?? "";
            const base2EditEnabled = isBase2EditGroupEnabled();
            if (
                currentStagesJson === lastKnownStagesJson &&
                base2EditEnabled === lastKnownBase2EditEnabled
            ) {
                return;
            }

            lastKnownStagesJson = currentStagesJson;
            lastKnownBase2EditEnabled = base2EditEnabled;
            publishStageAvailability();
        }, 150);
    };

    const scheduleStageSyncFromUi = (
        stageId: number,
        validateApplyAfter = false,
    ) => {
        const existing = stageSyncTimers.get(stageId);
        if (existing) {
            clearTimeout(existing);
        }

        const t = setTimeout(() => {
            try {
                syncSingleStageFromUi(stageId, validateApplyAfter);
            } catch {}
        }, 125);

        stageSyncTimers.set(stageId, t);
    };

    const syncSingleStageFromUi = (
        stageId: number,
        validateApplyAfterFlag: boolean,
    ) => {
        const stages = deps.getStages();
        const idx = stageId - 1;
        if (idx < 0 || idx >= stages.length) {
            return;
        }

        const prefix = `base2edit_stage_${stageId}_`;
        deps.updateStageFromUi(prefix, stages[idx]);
        deps.saveStages(stages);

        if (!validateApplyAfterFlag) {
            return;
        }

        const stageIds = [0, ...stages.map((_, i) => i + 1)];
        const applyElem = Utils.getSelectElement(`${prefix}applyafter`);
        if (applyElem) {
            cleanApplyAfterOptions(applyElem, stageIds, stageId);
            validateApplyAfter(prefix, stageIds, stageId);
        }
    };

    const installStageChangeListener = (editor: HTMLElement) => {
        if (changeListenerElem === editor) {
            return;
        }

        const handler = (e: Event) => {
            try {
                const target = e.target as Element;
                if (!target) {
                    return;
                }

                const stageWrap: HTMLElement | null = target.closest(
                    "[data-base2edit-stage-id]",
                );
                if (!stageWrap) {
                    return;
                }

                const stageId = parseInt(
                    stageWrap.dataset.base2editStageId ?? "0",
                    10,
                );
                if (stageId < 1) {
                    return;
                }

                if (
                    target.closest(
                        'button[data-base2edit-action="remove-stage"]',
                    )
                ) {
                    return;
                }

                const isApplyAfter = !!target.closest(
                    `#base2edit_stage_${stageId}_applyafter`,
                );
                scheduleStageSyncFromUi(stageId, isApplyAfter);
            } catch {}
        };

        editor.addEventListener("input", handler, true);
        editor.addEventListener("change", handler, true);
        changeListenerElem = editor;
    };

    const markPersisted = (json: string, enabled: boolean) => {
        lastKnownStagesJson = json;
        lastKnownBase2EditEnabled = enabled;
    };

    return {
        startPublishedStageSync,
        installStageChangeListener,
        publishStageAvailability,
        markPersisted,
    };
};
