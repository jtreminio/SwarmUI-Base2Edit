# SwarmUI Base2Edit Extension

Add an edit stage to your image generation workflow. Generate an image, then automatically edit it - all in one go.

## Usage

The edit stage can use the `<edit>` prompt tag. Everything inside `<edit>` becomes your edit instructions.
If you don't define an `<edit>` or `<edit[0]>` section, Base2Edit will use your normal (global) prompt as the edit prompt.

For multi-stage workflows, you can also target a specific edit stage using `<edit[n]>` (0-indexed):
- `<edit>` applies to **all** Base2Edit edit stages (including any LoRAs inside the section)
- `<edit[0]>` applies to **only** edit stage 0, `<edit[1]>` to stage 1, etc.
- If no `<edit>` / `<edit[n]>` section exists for a stage, that stage falls back to the global prompt.

You can also reuse another stage prompt inside an edit section with `<b2eprompt[n]>`:
- `n` can be `global`, `base`, `refiner`, or a 0-indexed edit stage number (`0`, `1`, `2`, ...)
- If the target stage prompt is not defined, it falls back to the global prompt
- The inserted text is the final parsed prompt text (not raw `<random>`, `<wildcard>`, `<lora>`, etc.)

Example:
```
global prompt
<base>base prompt
<refiner>refiner prompt
<edit[0]><b2eprompt[base]>
```

You can choose when the edit happens:
- **After Base** - Edit right after the initial generation, before any upscaling or refining
- **After Refiner** - Edit the final image after all other stages are done

When two or more edit stages share the same start (e.g. both "After Refiner"), they run **in parallel** from that point: each branch gets the same input image, runs its own edit, and only the **first** stage (lowest ID) continues into the rest of the pipeline. The other branches save their output and stop, so you get multiple edited variants from one run without chaining them.

## Quick Start

1. Toggle the **Base2Edit** group ON
2. Choose the edit model you want
3. Add `<edit>` to your prompt with what you want changed
4. Generate!

## Example Prompt

```
1girl, looking at viewer, smiling
<edit>Reskin this into a realistic photograph
```

The base model (and refiner if you enabled it) generates the girl, then the edit stage transforms it into a photo.

## Options

- **Keep Pre-Edit Image** - Save the image before editing so you can compare
- **Apply Edit After** - Choose to edit after Base or after Refiner
- **Edit Control** - How strongly to apply the edit (1.0 = full effect, lower = more subtle)

### Edit Overrides

The edit stage runs with its own settings. You can override (per edit stage):
- Model
- Steps
- VAE (toggleable)
- CFG Scale (toggleable)
- Sampler (toggleable)
- Scheduler (toggleable)

If an override is disabled/unset, Base2Edit inherits defaults from the stage your **Edit Model** points at:
- **(Use Base)** inherits Base stage sampling defaults (CFG/sampler/scheduler/VAE) and the Base model stack + lora
- **(Use Refiner)** inherits Refiner sampling defaults (CFG/sampler/scheduler/VAE) and the Refiner model stack + lora. If no Refiner stage defined, uses Base

#### LoRAs

- Any `<lora:...>` tags inside `<edit>` / `<edit[n]>` are stacked **on top** of the chosen model stack.
- If you pick **(Use Base)** or **(Use Refiner)**, the edit stage inherits that stage's model + LoRA stack (including UI-confined LoRAs).
- If you pick a specific model by name, Base2Edit loads that model and applies only **Global** UI LoRAs (plus any `<edit>` section LoRAs).
