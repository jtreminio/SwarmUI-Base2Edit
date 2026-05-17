import {
    buildChunkBar,
    buildGrid,
    renderSelectedList,
    updateSidebar,
} from "./render";
import { framePickerState } from "./state";

interface OpenResponse {
    error?: string;
    frameCount: number;
    fps: number;
    width: number;
    height: number;
    thumbUrlPattern: string;
    savedSelection: number[];
}

interface SaveResponse {
    error?: string;
    added: number[];
    removed: number[];
}

const MODAL_ID = "b2e-frame-picker-modal";

function getOrCreateModalEl(): HTMLElement {
    let el = document.getElementById(MODAL_ID);
    if (!el) {
        el = document.createElement("div");
        el.id = MODAL_ID;
        el.className = "modal fade b2e-fp-modal";
        el.tabIndex = -1;
        el.setAttribute("role", "dialog");
        document.body.appendChild(el);
    }
    return el;
}

export function openFramePickerModal(videoSrc: string): void {
    const modalEl = getOrCreateModalEl();
    modalEl.innerHTML = buildLoadingShell();
    ($(modalEl) as JQuery).modal("show");

    genericRequest("B2EFramePickerOpen", { videoUrl: videoSrc }, (raw) => {
        const data = raw as unknown as OpenResponse;
        if (data.error) {
            renderError(modalEl, data.error);
            return;
        }
        framePickerState.reset(data.savedSelection);
        renderModal(modalEl, videoSrc, data);
    });
}

function buildLoadingShell(): string {
    return `
        <div class="modal-dialog modal-xl" role="document">
            <div class="modal-content b2e-fp-modal-content">
                <div class="modal-header">
                    <h5 class="modal-title">Frame Picker</h5>
                    <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
                </div>
                <div class="modal-body b2e-fp-loading">
                    <div class="loading-spinner"><div class="loadspin1"></div><div class="loadspin2"></div><div class="loadspin3"></div></div>
                </div>
            </div>
        </div>`;
}

function renderError(modalEl: HTMLElement, message: string): void {
    const body = modalEl.querySelector(".modal-body");
    if (body) {
        body.innerHTML = `<div class="b2e-fp-error">${message}</div>`;
    }
}

function renderModal(
    modalEl: HTMLElement,
    videoSrc: string,
    data: OpenResponse,
): void {
    const modalDialog = modalEl.querySelector(".modal-dialog");
    if (!modalDialog) {
        return;
    }

    // Drive thumbnail aspect-ratio from real video dimensions.
    modalEl.style.setProperty(
        "--b2e-fp-aspect",
        `${data.width} / ${data.height}`,
    );

    const durationSec = (data.frameCount / data.fps).toFixed(1);
    const subtitle = `${durationSec}s · ${data.fps.toFixed(0)} fps · ${data.frameCount} frames · ${data.width}×${data.height}`;

    // Sidebar elements (built up-front so handlers can refer to them)
    const previewEl = document.createElement("div");
    previewEl.className = "b2e-fp-side-preview";
    previewEl.innerHTML = `<p class="b2e-fp-side-empty">Click a frame to preview</p>`;

    const selectedListEl = document.createElement("div");
    selectedListEl.className = "b2e-fp-selected-list";

    let lastFocused: number | null = null;

    const refreshSelectedList = (): void => {
        renderSelectedList(
            selectedListEl,
            data.fps,
            data.thumbUrlPattern,
            (fi) => focusFrame(fi),
            (fi) => deselectFrame(fi),
        );
    };

    const counterEl = document.createElement("span");
    counterEl.className = "b2e-fp-selection-count";
    counterEl.textContent = `${framePickerState.count()} selected`;
    const refreshCounter = (): void => {
        counterEl.textContent = `${framePickerState.count()} selected`;
    };

    const focusFrame = (frameIndex: number): void => {
        lastFocused = frameIndex;
        updateSidebar(previewEl, frameIndex, data.fps, data.thumbUrlPattern);
        const tile = modalEl.querySelector(
            `.b2e-fp-frame-tile[data-frame-index="${frameIndex}"]`,
        );
        tile?.scrollIntoView({ behavior: "smooth", block: "center" });
    };

    const deselectFrame = (frameIndex: number): void => {
        framePickerState.deselect(frameIndex);
        const tile = modalEl.querySelector(
            `.b2e-fp-frame-tile[data-frame-index="${frameIndex}"]`,
        );
        tile?.classList.remove("b2e-fp-selected");
        refreshCounter();
        refreshSelectedList();
        if (lastFocused === frameIndex) {
            updateSidebar(
                previewEl,
                frameIndex,
                data.fps,
                data.thumbUrlPattern,
            );
        }
    };

    const onFrameClick = (frameIndex: number, tileEl: HTMLElement): void => {
        framePickerState.toggle(frameIndex);
        tileEl.classList.toggle(
            "b2e-fp-selected",
            framePickerState.isSelected(frameIndex),
        );
        lastFocused = frameIndex;
        refreshCounter();
        updateSidebar(previewEl, frameIndex, data.fps, data.thumbUrlPattern);
        refreshSelectedList();
    };

    const chunkBar = buildChunkBar(data.frameCount, data.fps);
    const gridScroll = buildGrid({
        frameCount: data.frameCount,
        fps: data.fps,
        thumbUrlPattern: data.thumbUrlPattern,
        onFrameClick,
    });

    chunkBar
        .querySelectorAll(".b2e-fp-chunk-tab[data-chunk-index]")
        .forEach((tabEl) => {
            tabEl.addEventListener("click", () => {
                const ci = (tabEl as HTMLElement).dataset.chunkIndex;
                const section = gridScroll.querySelector(
                    `[data-chunk-index="${ci}"]`,
                );
                section?.scrollIntoView({ behavior: "smooth", block: "start" });
            });
        });

    const mainArea = document.createElement("div");
    mainArea.className = "b2e-fp-main";
    mainArea.appendChild(chunkBar);
    mainArea.appendChild(gridScroll);

    const aside = document.createElement("aside");
    aside.className = "b2e-fp-side";
    aside.innerHTML = `<h3 class="b2e-fp-side-title">Selected Frame</h3>`;
    aside.appendChild(previewEl);
    aside.appendChild(selectedListEl);

    const layout = document.createElement("div");
    layout.className = "b2e-fp-layout";
    layout.appendChild(mainArea);
    layout.appendChild(aside);

    const footer = document.createElement("div");
    footer.className = "modal-footer b2e-fp-footer";
    footer.appendChild(counterEl);

    const clearBtn = document.createElement("button");
    clearBtn.className = "basic-button";
    clearBtn.type = "button";
    clearBtn.textContent = "Clear all";
    clearBtn.addEventListener("click", () => {
        framePickerState.clearAll();
        modalEl.querySelectorAll(".b2e-fp-selected").forEach((el) => {
            el.classList.remove("b2e-fp-selected");
        });
        refreshCounter();
        refreshSelectedList();
    });

    const cancelBtn = document.createElement("button");
    cancelBtn.className = "basic-button";
    cancelBtn.type = "button";
    cancelBtn.textContent = "Cancel";
    cancelBtn.addEventListener("click", () =>
        ($(modalEl) as JQuery).modal("hide"),
    );

    const saveBtn = document.createElement("button");
    saveBtn.className = "basic-button b2e-fp-save-btn";
    saveBtn.type = "button";
    saveBtn.textContent = "Save selections";
    saveBtn.addEventListener("click", () => {
        saveBtn.disabled = true;
        saveBtn.textContent = "Saving…";
        genericRequest(
            "B2EFramePickerSave",
            { videoUrl: videoSrc, frameIndices: framePickerState.getAll() },
            (raw) => {
                const result = raw as unknown as SaveResponse;
                if (result.error) {
                    renderError(
                        modalEl,
                        `Frame Picker save failed: ${result.error}`,
                    );
                    saveBtn.disabled = false;
                    saveBtn.textContent = "Save selections";
                    return;
                }
                ($(modalEl) as JQuery).modal("hide");
            },
        );
    });

    footer.appendChild(clearBtn);
    footer.appendChild(cancelBtn);
    footer.appendChild(saveBtn);

    modalDialog.innerHTML = `
        <div class="modal-content b2e-fp-modal-content">
            <div class="modal-header">
                <h5 class="modal-title">Frame Picker <span class="b2e-fp-subtitle">${subtitle}</span></h5>
                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
            </div>
            <div class="modal-body b2e-fp-modal-body"></div>
        </div>`;
    const body = modalDialog.querySelector(".modal-body");
    body?.appendChild(layout);
    modalDialog.querySelector(".modal-content")?.appendChild(footer);

    refreshSelectedList();
}
