# SwarmUI Base2Edit Extension

Add an edit stage to your image generation workflow. Generate an image, then automatically edit it - all in one go.

## Usage

The edit stage uses the `<edit>` prompt tag. Everything inside `<edit>` becomes your edit instructions - the rest of your prompt is ignored during the edit stage.

For multi-stage workflows, you can also target a specific edit stage using `<edit[n]>` (0-indexed):
- `<edit>` applies to **all** Base2Edit edit stages (including any LoRAs inside the section)
- `<edit[0]>` applies to **only** edit stage 0, `<edit[1]>` to stage 1, etc.
- Base2Edit runs only if `<edit>` or `<edit[0]>` is present (and the Base2Edit group is enabled / Edit Model is set)

You can choose when the edit happens:
- **After Base** - Edit right after the initial generation, before any upscaling or refining
- **After Refiner** - Edit the final image after all other stages are done

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

The edit stage runs with its own settings. You can override:
- Model
- VAE  
- Steps
- CFG Scale
- Sampler
- Scheduler

If you don't set these, it uses whatever's currently active.
