# SwarmUI Base2Edit Extension

Edit an image in the refiner stage, allowing you to generate and edit in a single generation.

## Usage

1. Configure a base model and a refiner model
2. Set **Refiner Control to 1** for maximum effectiveness
3. Toggle the **Base2Edit** group ON
4. Use `<refiner>` tags for edit instructions: `<refiner>Reskin this into a realistic photograph`
5. Generate

## Full Prompt Example

```
<base>1girl, looking at viewer, smiling
<refiner>Reskin this into a realistic photograph
```

## Options

**Keep Base Image** - Saves the base image (before editing) alongside the final output.
