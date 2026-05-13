import { isBase2EditGroupEnabled } from "./rootStage";
import type { Stage } from "./types";
import { validateStages } from "./validation";

export type GenerateWrapApi = {
    tryWrap: () => void;
    startRetry: (intervalMs?: number) => void;
};

export type GenerateWrapDeps = {
    getStages: () => Stage[];
    serializeStagesFromUi: () => void;
};

export function createGenerateWrap(deps: GenerateWrapDeps): GenerateWrapApi {
    let genButtonWrapped = false;
    let genWrapInterval: ReturnType<typeof setInterval> | null = null;

    const tryWrap = () => {
        if (genButtonWrapped) {
            return;
        }
        if (typeof mainGenHandler === "undefined" || !mainGenHandler) {
            return;
        }
        if (typeof mainGenHandler.doGenerate !== "function") {
            return;
        }

        const original = mainGenHandler.doGenerate.bind(mainGenHandler);

        mainGenHandler.doGenerate = (...args: unknown[]) => {
            if (!isBase2EditGroupEnabled()) {
                return original(...args);
            }

            deps.serializeStagesFromUi();
            const errs = validateStages(deps.getStages());

            if (errs.length > 0) {
                showError(errs[0]);
                return;
            }

            return original(...args);
        };
        mainGenHandler.doGenerate.__base2editWrapped = true;
        genButtonWrapped = true;
    };

    const startRetry = (intervalMs = 250) => {
        if (genWrapInterval) {
            return;
        }

        const check = () => {
            try {
                tryWrap();
                if (
                    typeof mainGenHandler !== "undefined" &&
                    mainGenHandler &&
                    typeof mainGenHandler.doGenerate === "function" &&
                    mainGenHandler.doGenerate.__base2editWrapped
                ) {
                    if (genWrapInterval) {
                        clearInterval(genWrapInterval);
                        genWrapInterval = null;
                    }
                }
            } catch {}
        };

        check();
        genWrapInterval = setInterval(check, intervalMs);
    };

    return {
        tryWrap,
        startRetry,
    };
}
