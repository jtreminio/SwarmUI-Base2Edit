export const registerEditPromptPrefix = (): void => {
    promptTabComplete.registerPrefix(
        "edit",
        "Add a section of prompt text that is only used for Base2Edit edit stages.",
        () => [
            '\nUse "<edit>..." to apply to ALL Base2Edit edit stages (including LoRAs inside the section).',
            '\nUse "<edit[0]>..." to apply only to edit stage 0, "<edit[1]>..." for stage 1, etc.',
            '\nIf no "<edit>" / "<edit[0]>" section exists for a stage, Base2Edit falls back to the global prompt.',
        ],
        true,
    );
};

export const registerB2EPromptPrefix = (): void => {
    promptTabComplete.registerPrefix(
        "b2eprompt",
        "Use a Base2Edit prompt reference by stage: global, base, refiner, or edit stage number.",
        () => [
            '\nUse "<b2eprompt[global]>" to reuse the final global prompt.',
            '\nUse "<b2eprompt[base]>" / "<b2eprompt[refiner]>" to reuse that stage prompt (fallback to global if missing).',
            '\nUse "<b2eprompt[0]>", "<b2eprompt[1]>", etc. for edit stage index 0+ (0-indexed, fallback to global if undefined).',
        ],
        false,
    );

    promptTabComplete.registerPrefix(
        "b2eprompt[global]",
        "Base2Edit prompt reference: final global prompt text.",
        () => ['\nInserts "<b2eprompt[global]>"'],
        true,
    );

    promptTabComplete.registerPrefix(
        "b2eprompt[base]",
        "Base2Edit prompt reference: base prompt text (fallback to global if missing).",
        () => ['\nInserts "<b2eprompt[base]>"'],
        true,
    );

    promptTabComplete.registerPrefix(
        "b2eprompt[refiner]",
        "Base2Edit prompt reference: refiner prompt text (fallback to global if missing).",
        () => [
            '\nInserts "<b2eprompt[refiner]>"',
            '\nFor edit stages, use numeric index 0+ (example: "<b2eprompt[0]>").',
        ],
        true,
    );
};

export const registerB2EImagePrefix = (): void => {
    promptTabComplete.registerPrefix(
        "b2eimage",
        "Use an image reference from an earlier stage inside an <edit> section.",
        () => [
            '\nUse "<b2eimage[base]>" to reference the base-stage image.',
            '\nUse "<b2eimage[refiner]>" to reference the refiner-stage image (only available in final-stage edits).',
            '\nUse "<b2eimage[edit0]>" for earlier edit stages.',
            '\nUse "<b2eimage[prompt0]>" for prompt image index 0.',
        ],
        false,
    );

    promptTabComplete.registerPrefix(
        "b2eimage[base]",
        "Base2Edit image reference: base stage image.",
        () => ['\nInserts "<b2eimage[base]>"'],
        true,
    );

    promptTabComplete.registerPrefix(
        "b2eimage[refiner]",
        "Base2Edit image reference: refiner stage image.",
        () => ['\nInserts "<b2eimage[refiner]>"'],
        true,
    );

    promptTabComplete.registerPrefix(
        "b2eimage[edit0]",
        "Base2Edit image reference: edit stage 0 output image.",
        () => ['\nInserts "<b2eimage[edit0]>"'],
        true,
    );

    promptTabComplete.registerPrefix(
        "b2eimage[prompt0]",
        "Base2Edit image reference: prompt image 0.",
        () => ['\nInserts "<b2eimage[prompt0]>"'],
        true,
    );
};
