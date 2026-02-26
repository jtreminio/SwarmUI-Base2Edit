using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Image = SwarmUI.Utils.Image;

namespace Base2Edit;

public partial class EditStage
{
    private const string B2EImageReferenceStoreKey = "base2edit.b2eimage.reference_store";

    private enum B2EImageReferenceKind
    {
        Base,
        Refiner,
        EditStage,
        PromptImage
    }

    private sealed record B2EImageReference(
        B2EImageReferenceKind Kind,
        int Index,
        string NormalizedTarget,
        string RawTarget
    );

    private sealed record B2EImagePromptParseResult(
        EditPrompts Prompts,
        List<B2EImageReference> References
    );

    private sealed record B2EImageStageReference(
        JArray Samples,
        JArray Vae,
        JArray Image
    );

    private sealed class B2EImageReferenceStore
    {
        public B2EImageStageReference Base;
        public B2EImageStageReference Refiner;
        public Dictionary<int, B2EImageStageReference> EditStages { get; } = [];
    }

    private static void ResetB2EImageReferenceStore(WorkflowGenerator g)
    {
        g.UserInput.ExtraMeta[B2EImageReferenceStoreKey] = new B2EImageReferenceStore();
    }

    private static void CaptureBaseAnchorForB2EImage(WorkflowGenerator g)
    {
        B2EImageReferenceStore store = GetOrCreateB2EImageReferenceStore(g.UserInput);
        if (store.Base is not null)
        {
            return;
        }

        B2EImageStageReference snap = SnapshotCurrentStageReference(g);
        if (snap is not null)
        {
            store.Base = snap;
        }
    }

    private static void CaptureRefinerAnchorForB2EImage(WorkflowGenerator g)
    {
        B2EImageReferenceStore store = GetOrCreateB2EImageReferenceStore(g.UserInput);
        B2EImageStageReference snap = SnapshotCurrentStageReference(g);
        if (snap is not null)
        {
            store.Refiner = snap;
        }
    }

    private static void CaptureEditStageOutputForB2EImage(WorkflowGenerator g, int stageIndex)
    {
        if (stageIndex < 0)
        {
            return;
        }

        B2EImageReferenceStore store = GetOrCreateB2EImageReferenceStore(g.UserInput);
        B2EImageStageReference snap = SnapshotCurrentStageReference(g);
        if (snap is not null)
        {
            store.EditStages[stageIndex] = snap;
        }
    }

    private static B2EImageStageReference SnapshotCurrentStageReference(WorkflowGenerator g)
    {
        JArray samples = CloneNodeRef(TryGetCurrentSamplesRef(g));
        JArray vae = CloneNodeRef(TryGetCurrentVaeRef(g));
        JArray image = CloneNodeRef(TryGetCurrentImageRef(g));
        return SnapshotStageReference(samples, vae, image);
    }

    private static JArray TryGetCurrentSamplesRef(WorkflowGenerator g)
    {
        if (g.CurrentMedia?.IsLatentData == true && g.CurrentMedia.Path is JArray latentPath)
        {
            return latentPath;
        }

        try
        {
            return g.CurrentMedia?.AsLatentImage(g.CurrentVae)?.Path;
        }
        catch
        {
            return null;
        }
    }

    private static JArray TryGetCurrentImageRef(WorkflowGenerator g)
    {
        if (g.CurrentMedia?.IsRawMedia == true && g.CurrentMedia.Path is JArray imagePath)
        {
            return imagePath;
        }

        try
        {
            return g.CurrentMedia?.AsRawImage(g.CurrentVae)?.Path;
        }
        catch
        {
            return null;
        }
    }

    private static JArray TryGetCurrentVaeRef(WorkflowGenerator g)
    {
        if (g.CurrentVae?.Path is JArray currentVaePath)
        {
            return currentVaePath;
        }

        try
        {
            return g.CurrentVae?.Path;
        }
        catch
        {
            return null;
        }
    }

    private static B2EImageStageReference SnapshotStageReference(JArray samples, JArray vae, JArray image)
    {
        JArray snapSamples = CloneNodeRef(samples);
        JArray snapVae = CloneNodeRef(vae);
        JArray snapImage = CloneNodeRef(image);

        if (snapSamples is null && snapImage is null)
        {
            return null;
        }

        return new B2EImageStageReference(snapSamples, snapVae, snapImage);
    }

    private static B2EImageReferenceStore GetOrCreateB2EImageReferenceStore(T2IParamInput input)
    {
        if (input.ExtraMeta is null)
        {
            return new B2EImageReferenceStore();
        }

        if (input.ExtraMeta.TryGetValue(B2EImageReferenceStoreKey, out object existingObj)
            && existingObj is B2EImageReferenceStore existing)
        {
            return existing;
        }

        B2EImageReferenceStore created = new();
        input.ExtraMeta[B2EImageReferenceStoreKey] = created;
        return created;
    }

    private static B2EImagePromptParseResult ParseB2EImageTags(EditPrompts prompts, int stageIndex)
    {
        List<B2EImageReference> references = [];
        HashSet<string> dedupe = [];

        string positive = StripB2EImageTagsFromPrompt(prompts.Positive, stageIndex, references, dedupe);
        string negative = StripB2EImageTagsFromPrompt(prompts.Negative, stageIndex, references, dedupe);

        return new B2EImagePromptParseResult(
            new EditPrompts((positive ?? "").Trim(), (negative ?? "").Trim()),
            references
        );
    }

    private static string StripB2EImageTagsFromPrompt(
        string promptText,
        int stageIndex,
        List<B2EImageReference> references,
        HashSet<string> dedupe)
    {
        if (string.IsNullOrWhiteSpace(promptText) || !promptText.Contains("<b2eimage", StringComparison.OrdinalIgnoreCase))
        {
            return promptText ?? "";
        }

        StringBuilder output = new(promptText.Length);
        int cursor = 0;
        while (cursor < promptText.Length)
        {
            int open = promptText.IndexOf('<', cursor);
            if (open == -1)
            {
                output.Append(promptText[cursor..]);
                break;
            }

            if (open > cursor)
            {
                output.Append(promptText[cursor..open]);
            }

            int close = promptText.IndexOf('>', open + 1);
            if (close == -1)
            {
                output.Append(promptText[open..]);
                break;
            }

            string tag = promptText[(open + 1)..close];
            if (TryExtractTagPrefix(tag, out string prefixName, out string preData)
                && string.Equals(prefixName, "b2eimage", StringComparison.OrdinalIgnoreCase))
            {
                if (TryNormalizeB2EImageReference(preData, stageIndex, out B2EImageReference reference, out string warning))
                {
                    if (dedupe.Add(reference.NormalizedTarget))
                    {
                        references.Add(reference);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(warning))
                {
                    Logs.Warning(warning);
                }
            }
            else
            {
                output.Append(promptText[open..(close + 1)]);
            }

            cursor = close + 1;
        }

        return output.ToString();
    }

    private static bool TryNormalizeB2EImageReference(
        string preData,
        int stageIndex,
        out B2EImageReference reference,
        out string warning)
    {
        reference = null;
        warning = null;

        string raw = string.IsNullOrWhiteSpace(preData) ? "" : preData.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            warning = $"Base2Edit: Invalid <b2eimage[]> in stage {stageIndex}; expected [base], [refiner], [editN], or [promptN].";
            return false;
        }

        string lower = raw.ToLowerInvariant();
        if (lower == "base")
        {
            reference = new B2EImageReference(B2EImageReferenceKind.Base, 0, "base", raw);
            return true;
        }

        if (lower == "refiner")
        {
            reference = new B2EImageReference(B2EImageReferenceKind.Refiner, 0, "refiner", raw);
            return true;
        }

        if (lower.StartsWith("edit", StringComparison.Ordinal)
            && int.TryParse(lower["edit".Length..], out int editIndex)
            && editIndex >= 0)
        {
            if (editIndex >= stageIndex)
            {
                warning = $"Base2Edit: Invalid <b2eimage[{raw}]> in stage {stageIndex}; edit references must target an earlier stage.";
                return false;
            }

            reference = new B2EImageReference(B2EImageReferenceKind.EditStage, editIndex, $"edit:{editIndex}", raw);
            return true;
        }

        if (lower.StartsWith("prompt", StringComparison.Ordinal)
            && int.TryParse(lower["prompt".Length..], out int promptIndex)
            && promptIndex >= 0)
        {
            reference = new B2EImageReference(B2EImageReferenceKind.PromptImage, promptIndex, $"prompt:{promptIndex}", raw);
            return true;
        }

        warning = $"Base2Edit: Invalid <b2eimage[{raw}]> in stage {stageIndex}; expected [base], [refiner], [editN], or [promptN].";
        return false;
    }

    private static List<JArray> ResolveB2EImageReferenceLatents(
        WorkflowGenerator g,
        IReadOnlyList<B2EImageReference> references,
        int stageIndex,
        JArray currentStageVae)
    {
        List<JArray> resolved = [];

        if (references is null || references.Count == 0)
        {
            return resolved;
        }

        B2EImageReferenceStore store = GetOrCreateB2EImageReferenceStore(g.UserInput);

        foreach (B2EImageReference reference in references)
        {
            JArray resolvedLatent = reference.Kind switch
            {
                B2EImageReferenceKind.Base => ResolveStoredStageReference(g, store.Base, reference, stageIndex, currentStageVae),
                B2EImageReferenceKind.Refiner => ResolveStoredStageReference(g, store.Refiner, reference, stageIndex, currentStageVae),
                B2EImageReferenceKind.EditStage => ResolveEditStageReference(g, store, reference, stageIndex, currentStageVae),
                B2EImageReferenceKind.PromptImage => ResolvePromptImageReference(g, reference, stageIndex, currentStageVae),
                _ => null
            };

            if (resolvedLatent is null)
            {
                continue;
            }

            // Current-stage latent is always appended by the existing final ReferenceLatent node;
            // avoid generating a duplicate no-op chain entry when a b2eimage resolves to the same latent.
            JArray currentSamples = TryGetCurrentSamplesRef(g);
            if (currentSamples is not null && JToken.DeepEquals(resolvedLatent, currentSamples))
            {
                continue;
            }

            bool alreadyAdded = false;
            foreach (JArray existing in resolved)
            {
                if (JToken.DeepEquals(existing, resolvedLatent))
                {
                    alreadyAdded = true;
                    break;
                }
            }

            if (!alreadyAdded)
            {
                resolved.Add(resolvedLatent);
            }
        }

        return resolved;
    }

    private static JArray ResolveEditStageReference(
        WorkflowGenerator g,
        B2EImageReferenceStore store,
        B2EImageReference reference,
        int stageIndex,
        JArray currentStageVae)
    {
        if (reference.Index >= stageIndex)
        {
            Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]> in stage {stageIndex}; edit references must target an earlier stage.");
            return null;
        }

        if (!store.EditStages.TryGetValue(reference.Index, out B2EImageStageReference stageRef) || stageRef is null)
        {
            Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]> in stage {stageIndex}; referenced edit stage output is unavailable.");
            return null;
        }

        return ResolveStoredStageReference(g, stageRef, reference, stageIndex, currentStageVae);
    }

    private static JArray ResolvePromptImageReference(
        WorkflowGenerator g,
        B2EImageReference reference,
        int stageIndex,
        JArray currentStageVae)
    {
        if (currentStageVae is null)
        {
            Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]> in stage {stageIndex}; current stage VAE is unavailable.");
            return null;
        }

        if (!g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> promptImages)
            || promptImages is null
            || reference.Index < 0
            || reference.Index >= promptImages.Count)
        {
            Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]> in stage {stageIndex}; referenced prompt image is unavailable.");
            return null;
        }

        WGNodeData loadedImage = g.LoadImage(promptImages[reference.Index], $"${{promptimages.{reference.Index}}}", false);
        JArray imageRef = loadedImage.Path;

        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, imageRef, currentStageVae, out JArray reusedSamples))
        {
            return CloneNodeRef(reusedSamples);
        }

        string encodeNode = g.CreateVAEEncode(currentStageVae, imageRef);
        return [encodeNode, 0];
    }

    private static JArray ResolveStoredStageReference(
        WorkflowGenerator g,
        B2EImageStageReference stageRef,
        B2EImageReference reference,
        int stageIndex,
        JArray currentStageVae)
    {
        if (stageRef is null)
        {
            Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]> in stage {stageIndex}; referenced stage output is unavailable.");
            return null;
        }

        if (stageRef.Samples is null && stageRef.Image is null)
        {
            Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]> in stage {stageIndex}; referenced stage has no image/latent output.");
            return null;
        }

        if (currentStageVae is null)
        {
            if (stageRef.Samples is not null)
            {
                return CloneNodeRef(stageRef.Samples);
            }

            Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]> in stage {stageIndex}; current stage VAE is unavailable.");
            return null;
        }

        if (stageRef.Samples is not null
            && stageRef.Vae is not null
            && JToken.DeepEquals(stageRef.Vae, currentStageVae))
        {
            return CloneNodeRef(stageRef.Samples);
        }

        JArray imageRef = CloneNodeRef(stageRef.Image);
        if (imageRef is null)
        {
            if (stageRef.Samples is null || stageRef.Vae is null)
            {
                Logs.Warning($"Base2Edit: Ignoring <b2eimage[{reference.RawTarget}]> in stage {stageIndex}; cannot VAE-convert the referenced stage output.");
                return null;
            }

            if (VaeNodeReuse.ReuseVaeDecodeForSamplesAndVae(g, stageRef.Samples, stageRef.Vae, out JArray reusedImage))
            {
                imageRef = CloneNodeRef(reusedImage);
            }
            else
            {
                WGNodeData decoded = new WGNodeData(stageRef.Samples, g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat())
                    .DecodeLatents(new WGNodeData(stageRef.Vae, g, WGNodeData.DT_VAE, g.CurrentCompat()), false);
                imageRef = decoded.Path;
            }
        }

        if (VaeNodeReuse.ReuseVaeEncodeForImage(g, imageRef, currentStageVae, out JArray reusedEncoded))
        {
            return CloneNodeRef(reusedEncoded);
        }

        string encode = g.CreateVAEEncode(currentStageVae, imageRef);
        return [encode, 0];
    }

    private static JArray CloneNodeRef(JArray nodeRef)
    {
        if (nodeRef is null || nodeRef.Count != 2)
        {
            return null;
        }

        return new JArray(nodeRef[0], nodeRef[1]);
    }
}
