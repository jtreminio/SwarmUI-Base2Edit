import { framePickerState } from "./state";

/** Convert 0-based frameIndex → 1-based padded string for URL substitution. */
function frameToFileNum(frameIndex: number): string {
    return String(frameIndex + 1).padStart(6, "0");
}

export function thumbUrl(pattern: string, frameIndex: number): string {
    return pattern.replace("NNN", frameToFileNum(frameIndex));
}

export interface RenderOptions {
    frameCount: number;
    fps: number;
    thumbUrlPattern: string;
    onFrameClick: (frameIndex: number, tileEl: HTMLElement) => void;
}

/**
 * Build and return the <div class="chunks-scroll"> content element.
 * Chunks are sized to Math.round(fps) frames (≈ 1 second each).
 */
export function buildGrid(opts: RenderOptions): HTMLElement {
    const chunkSize = Math.max(1, Math.round(opts.fps));
    const scroll = document.createElement("div");
    scroll.className = "b2e-fp-chunks-scroll";

    let chunkIndex = 0;
    for (
        let startFrame = 0;
        startFrame < opts.frameCount;
        startFrame += chunkSize
    ) {
        const endFrame = Math.min(
            startFrame + chunkSize - 1,
            opts.frameCount - 1,
        );
        const chunkIndices: number[] = [];
        for (let i = startFrame; i <= endFrame; i++) {
            chunkIndices.push(i);
        }

        const startSec = (startFrame / opts.fps).toFixed(1);
        const endSec = ((endFrame + 1) / opts.fps).toFixed(1);

        const section = document.createElement("section");
        section.className = "b2e-fp-chunk-section";
        section.dataset.chunkIndex = String(chunkIndex);

        const head = document.createElement("div");
        head.className = "b2e-fp-chunk-section-head";
        head.innerHTML = `
            <div class="b2e-fp-chunk-section-title">
                Chunk ${chunkIndex + 1}
                <span class="b2e-fp-range">${startSec}–${endSec}s · frames ${startFrame}–${endFrame}</span>
            </div>`;

        const grid = document.createElement("div");
        grid.className = "b2e-fp-frame-grid";

        for (const fi of chunkIndices) {
            const tile = document.createElement("div");
            tile.className = "b2e-fp-frame-tile";
            tile.dataset.frameIndex = String(fi);
            if (framePickerState.isSelected(fi)) {
                tile.classList.add("b2e-fp-selected");
            }
            tile.innerHTML = `
                <img class="b2e-fp-thumb" src="${thumbUrl(opts.thumbUrlPattern, fi)}" alt="Frame ${fi}" loading="lazy">
                <span class="b2e-fp-frame-num">#${fi}</span>
                <span class="b2e-fp-time-label">${(fi / opts.fps).toFixed(2)}s</span>`;
            tile.addEventListener("click", () => {
                opts.onFrameClick(fi, tile);
            });
            grid.appendChild(tile);
        }

        section.appendChild(head);
        section.appendChild(grid);
        scroll.appendChild(section);
        chunkIndex++;
    }
    return scroll;
}

/**
 * Build the chunk-tab bar. Returns the bar element.
 */
export function buildChunkBar(frameCount: number, fps: number): HTMLElement {
    const chunkSize = Math.max(1, Math.round(fps));
    const bar = document.createElement("div");
    bar.className = "b2e-fp-chunk-bar";
    const left = document.createElement("div");
    left.className = "b2e-fp-chunk-bar-left";

    const allTab = document.createElement("button");
    allTab.className = "b2e-fp-chunk-tab b2e-fp-active";
    allTab.type = "button";
    allTab.innerHTML = `All <span class="browser-header-count">${frameCount}</span>`;
    left.appendChild(allTab);

    let chunkIndex = 0;
    for (let startFrame = 0; startFrame < frameCount; startFrame += chunkSize) {
        const endFrame = Math.min(startFrame + chunkSize - 1, frameCount - 1);
        const startSec = (startFrame / fps).toFixed(1);
        const endSec = ((endFrame + 1) / fps).toFixed(1);
        const tab = document.createElement("button");
        tab.className = "b2e-fp-chunk-tab";
        tab.type = "button";
        tab.dataset.chunkIndex = String(chunkIndex);
        tab.innerHTML = `${startSec}–${endSec}s <span class="browser-header-count">${endFrame - startFrame + 1}</span>`;
        left.appendChild(tab);
        chunkIndex++;
    }
    bar.appendChild(left);
    return bar;
}

/** Update sidebar preview for the focused frame. */
export function updateSidebar(
    sideEl: HTMLElement,
    frameIndex: number,
    fps: number,
    thumbUrlPattern: string,
): void {
    const timestamp = (frameIndex / fps).toFixed(3);
    sideEl.innerHTML = `
        <img class="b2e-fp-preview-img" src="${thumbUrl(thumbUrlPattern, frameIndex)}" alt="Frame ${frameIndex}">
        <dl class="b2e-fp-preview-meta">
            <dt>Frame #</dt><dd>${frameIndex}</dd>
            <dt>Timestamp</dt><dd>${timestamp}s</dd>
        </dl>`;
}

/**
 * Render the list of currently-selected frames as small thumbnails.
 * Clicking a thumb invokes onClick (e.g., scroll to + preview); clicking
 * the remove button invokes onRemove.
 */
export function renderSelectedList(
    listEl: HTMLElement,
    fps: number,
    thumbUrlPattern: string,
    onClick: (frameIndex: number) => void,
    onRemove: (frameIndex: number) => void,
): void {
    const indices = framePickerState.getAll();
    listEl.innerHTML = "";
    if (indices.length === 0) {
        listEl.innerHTML = `<p class="b2e-fp-selected-empty">No frames selected yet.</p>`;
        return;
    }
    const header = document.createElement("div");
    header.className = "b2e-fp-selected-header";
    header.textContent = `Selected (${indices.length})`;
    listEl.appendChild(header);

    const grid = document.createElement("div");
    grid.className = "b2e-fp-selected-grid";
    for (const fi of indices) {
        const item = document.createElement("div");
        item.className = "b2e-fp-selected-item";
        item.title = `Frame #${fi} · ${(fi / fps).toFixed(2)}s`;
        item.innerHTML = `
            <img class="b2e-fp-selected-thumb" src="${thumbUrl(thumbUrlPattern, fi)}" alt="Frame ${fi}" loading="lazy">
            <span class="b2e-fp-selected-num">#${fi}</span>
            <button class="b2e-fp-selected-remove" type="button" title="Deselect this frame">×</button>`;
        item.querySelector(".b2e-fp-selected-thumb")?.addEventListener(
            "click",
            () => onClick(fi),
        );
        item.querySelector(".b2e-fp-selected-num")?.addEventListener(
            "click",
            () => onClick(fi),
        );
        item.querySelector(".b2e-fp-selected-remove")?.addEventListener(
            "click",
            (e) => {
                e.stopPropagation();
                onRemove(fi);
            },
        );
        grid.appendChild(item);
    }
    listEl.appendChild(grid);
}
