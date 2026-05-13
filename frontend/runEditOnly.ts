export function runEditOnlyFromImage(src: string): void {
    if (!src) {
        showError("Cannot run Base2Edit: no image selected.");
        return;
    }

    const tmpImg = new Image();
    tmpImg.crossOrigin = "Anonymous";
    tmpImg.onerror = () =>
        showError("Cannot run Base2Edit: failed to load image.");
    tmpImg.onload = () => {
        const runWithUrl = (url: string) => {
            mainGenHandler.doGenerate({
                initimage: url,
                initimagecreativity: 0,
                images: 1,
                steps: 0,
                aspectratio: "Custom",
                width: tmpImg.naturalWidth,
                height: tmpImg.naturalHeight,
                applyeditafter: "Base",
                refinermethod: null,
                refinercontrolpercentage: null,
                refinerupscale: null,
            });
        };

        if (src.startsWith("data:")) {
            runWithUrl(src);
            return;
        }

        toDataURL(src, runWithUrl);
    };
    tmpImg.src = src;
}
