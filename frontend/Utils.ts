const Utils = {
    getInputElement: (id: string): HTMLInputElement | null => {
        return document.getElementById(id) as HTMLInputElement | null;
    },
    getSelectElement: (id: string): HTMLSelectElement | null => {
        return document.getElementById(id) as HTMLSelectElement | null;
    },
    getButtonElement: (id: string): HTMLButtonElement | null => {
        return document.getElementById(id) as HTMLButtonElement | null;
    },
}