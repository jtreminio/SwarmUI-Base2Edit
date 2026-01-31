declare const mainGenHandler: {
    doGenerate: ((...args: unknown[]) => void) & { __base2editWrapped?: boolean };
};

declare function showError(message: string): void;
declare function triggerChangeFor(element: HTMLElement): void;
declare function doToggleEnable(prefix: string): void;
declare function findParentOfClass(element: Element, className: string): HTMLElement;
declare function getHtmlForParam(param: Record<string, any>, prefix: string): { html: string; runnable: () => void };
declare function toDataURL(src: string, callback: (dataUrl: string) => void): void;

declare const promptTabComplete: {
    registerPrefix(prefix: string, description: string, hintFn: () => string[], ...args: unknown[]): void;
};

declare var isVideoExt: ((src: string) => boolean) | undefined;
declare var isAudioExt: ((src: string) => boolean) | undefined;
declare var buttonsForImage: ((fullsrc: string, src: string, metadata: unknown) => Array<{ label: string; title: string; onclick: () => void }>) | undefined;

declare let postParamBuildSteps: (() => void)[] | undefined;

interface Window {
    base2editRunEditOnlyFromImage?: (src: string) => void;
}
