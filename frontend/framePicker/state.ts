let _selection: Set<number> = new Set();
let _dirty = false;

export const framePickerState = {
    reset(savedSelection: number[]): void {
        _selection = new Set(savedSelection);
        _dirty = false;
    },

    toggle(frameIndex: number): void {
        if (_selection.has(frameIndex)) {
            _selection.delete(frameIndex);
        } else {
            _selection.add(frameIndex);
        }
        _dirty = true;
    },

    select(frameIndex: number): void {
        _selection.add(frameIndex);
        _dirty = true;
    },

    deselect(frameIndex: number): void {
        _selection.delete(frameIndex);
        _dirty = true;
    },

    selectAll(indices: number[]): void {
        for (const i of indices) {
            _selection.add(i);
        }
        _dirty = true;
    },

    clearAll(): void {
        _selection.clear();
        _dirty = true;
    },

    isSelected(frameIndex: number): boolean {
        return _selection.has(frameIndex);
    },

    getAll(): number[] {
        return [..._selection].sort((a, b) => a - b);
    },

    isDirty(): boolean {
        return _dirty;
    },

    count(): number {
        return _selection.size;
    },
};
