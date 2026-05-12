using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

internal static class PromptParser
{
    private const string EditTagName = "edit";
    private const string EditOpenTag = "<edit";
    private const string EditCidMarker = "//cid=";
    private const string B2EImageTagName = "b2eimage";
    private const string B2EImageOpenTag = "<b2eimage";
    private const string B2EPromptTagName = "b2eprompt";
    private const string B2EPromptOpenTag = "<b2eprompt";
    private const int NoMatchCid = -1;

    private static readonly HashSet<string> BuiltInSectionStarters = [
        "base",
        "refiner",
        "video",
        "videoswap",
        "videoclip",
        "region",
        "segment",
        "object",
        "clear",
        "extend"
    ];

    public record EditPrompts(string Positive, string Negative);

    public sealed record ImagePromptParseResult(
        EditPrompts Prompts,
        List<StageResolver.ImageReference> References
    );

    public static ImagePromptParseResult ParseImageTags(EditPrompts prompts, int index)
    {
        List<StageResolver.ImageReference> references = [];

        string positive = StripImageTagsFromPrompt(prompts.Positive, index, references);

        return new ImagePromptParseResult(
            new EditPrompts(positive, prompts.Negative),
            references
        );
    }

    private static bool TryExtractTagPrefix(string tag, out string prefixName, out string preData)
    {
        prefixName = null;
        preData = null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        string prefixPart = tag;
        int colon = tag.IndexOf(':');
        if (colon != -1)
        {
            prefixPart = tag[..colon];
        }
        prefixPart = prefixPart.Split('/')[0];
        if (string.IsNullOrWhiteSpace(prefixPart))
        {
            return false;
        }

        prefixName = prefixPart;
        if (prefixName.EndsWith(']') && prefixName.Contains('['))
        {
            int open = prefixName.LastIndexOf('[');
            if (open != -1)
            {
                preData = prefixName[(open + 1)..^1];
                prefixName = prefixName[..open];
            }
        }

        return !string.IsNullOrWhiteSpace(prefixName);
    }

    private static bool IsSectionStartingTag(string tagPrefixLower)
    {
        if (BuiltInSectionStarters.Contains(tagPrefixLower))
        {
            return true;
        }
        foreach (string prefix in PromptRegion.CustomPartPrefixes)
        {
            if (string.Equals(prefix, tagPrefixLower, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string StripImageTagsFromPrompt(
        string promptText,
        int index,
        List<StageResolver.ImageReference> references)
    {
        promptText ??= "";
        HashSet<string> dedupe = [];

        if (!promptText.Contains(B2EImageOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return promptText;
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
                && StringUtils.Equals(prefixName, B2EImageTagName))
            {
                StageResolver.ImageReference reference = NormalizeImageReference(preData, index);
                if (dedupe.Add(reference.NormalizedTarget))
                {
                    references.Add(reference);
                }
            }
            else
            {
                output.Append(promptText[open..(close + 1)]);
            }

            cursor = close + 1;
        }

        return output.ToString().Trim();
    }

    private static StageResolver.ImageReference NormalizeImageReference(
        string preData,
        int index)
    {
        string raw = string.IsNullOrWhiteSpace(preData) ? "" : preData.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SwarmUserErrorException(
                $"Base2Edit: Invalid <b2eimage> tag in stage {index}: missing reference target. "
                + "Expected [base], [refiner], [editN]/[Edit Stage N], or [promptN].");
        }

        string lower = raw.ToLowerInvariant();
        if (lower == "base")
        {
            return new StageResolver.ImageReference(
                StageRefStore.StageKind.Base,
                0,
                "base",
                raw
            );
        }

        if (lower == "refiner")
        {
            return new StageResolver.ImageReference(
                StageRefStore.StageKind.Refiner,
                0,
                "refiner",
                raw
            );
        }

        if (StageRefStore.TryParseStageIndexKey(raw, out int editIndex))
        {
            if (editIndex >= index)
            {
                throw new SwarmUserErrorException(
                    $"Base2Edit: Invalid <b2eimage[{raw}]> tag in stage {index}: "
                    + "edit references must target an earlier stage.");
            }

            return new StageResolver.ImageReference(
                StageRefStore.StageKind.Edit,
                editIndex,
                $"edit:{editIndex}",
                raw
            );
        }

        if (lower.StartsWith("prompt", StringComparison.Ordinal)
            && int.TryParse(lower["prompt".Length..], out int promptIndex)
            && promptIndex >= 0)
        {
            return new StageResolver.ImageReference(
                StageRefStore.StageKind.Prompt,
                promptIndex,
                $"prompt:{promptIndex}",
                raw
            );
        }

        throw new SwarmUserErrorException(
            $"Base2Edit: Invalid <b2eimage[{raw}]> tag in stage {index}: unrecognized target '{raw}'. "
            + "Expected [base], [refiner], [editN]/[Edit Stage N], or [promptN].");
    }

    public static bool HasAnyEditSectionForStage(string prompt, int stageIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains(EditOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int globalCid = Base2EditExtension.SectionID_Edit;
        int stageCid = Base2EditExtension.EditSectionIdForStage(stageIndex);
        string canonical = CanonicalizeEditBrackets(prompt, stageIndex, globalCid, stageCid);

        foreach (PromptRegion.Part part in new PromptRegion(canonical).Parts)
        {
            if (part.Prefix == EditTagName
                && (part.ContextID == globalCid || part.ContextID == stageCid))
            {
                return true;
            }
        }
        return false;
    }

    public static string ExtractPrompt(string prompt, string originalPrompt, int stageIndex)
    {
        string extracted = ExtractPromptWithoutB2EPrompt(prompt, stageIndex);
        string resolved = ResolveB2EPromptTags(prompt, extracted, stageIndex, []);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        if (!ShouldFallbackForTagOnlyEditSection(prompt, originalPrompt, stageIndex))
        {
            return resolved.Trim();
        }

        for (int prevStage = stageIndex - 1; prevStage >= 0; prevStage--)
        {
            string prevPrompt = ExtractPrompt(prompt, originalPrompt, prevStage);
            if (!string.IsNullOrWhiteSpace(prevPrompt))
            {
                return prevPrompt;
            }
        }

        return GetGlobalPromptText(prompt);
    }

    public static string GetOriginalPrompt(T2IParamInput input, string paramId, string fallback)
    {
        if (input.ExtraMeta is not null
            && input.ExtraMeta.TryGetValue($"original_{paramId}", out object originalObj)
            && originalObj is string originalPrompt)
        {
            return originalPrompt;
        }

        return fallback ?? "";
    }

    private static bool ShouldFallbackForTagOnlyEditSection(string parsedPrompt, string originalPrompt, int stageIndex)
    {
        if (stageIndex < 0 || !HasAnyEditSectionForStage(parsedPrompt, stageIndex))
        {
            return false;
        }

        string sourcePrompt = string.IsNullOrWhiteSpace(originalPrompt) ? parsedPrompt : originalPrompt;
        if (!HasAnyEditSectionForStage(sourcePrompt, stageIndex))
        {
            return false;
        }

        string sourceSection = ExtractPromptWithoutB2EPrompt(sourcePrompt, stageIndex);
        if (string.IsNullOrWhiteSpace(sourceSection))
        {
            return false;
        }

        if (sourceSection.Contains(B2EImageOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!sourceSection.Contains('<'))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(StripPromptTags(sourceSection));
    }

    private static string StripPromptTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        if (!text.Contains('<'))
        {
            return text;
        }

        StringBuilder cleaned = new(text.Length);
        bool inTag = false;
        foreach (char c in text)
        {
            if (!inTag)
            {
                if (c == '<')
                {
                    inTag = true;
                }
                else
                {
                    cleaned.Append(c);
                }
            }
            else if (c == '>')
            {
                inTag = false;
            }
        }
        return cleaned.ToString();
    }

    private static string ExtractPromptWithoutB2EPrompt(string prompt, int stageIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "";
        }
        if (!prompt.Contains(EditOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return prompt.Trim();
        }

        int globalCid = Base2EditExtension.SectionID_Edit;
        int stageCid = Base2EditExtension.EditSectionIdForStage(stageIndex);
        string canonical = CanonicalizeEditBrackets(prompt, stageIndex, globalCid, stageCid);

        StringBuilder result = new();
        bool sawRelevant = false;
        foreach (PromptRegion.Part part in new PromptRegion(canonical).Parts)
        {
            if (part.Prefix != EditTagName)
            {
                continue;
            }
            int cid = part.ContextID;
            if (cid == globalCid || cid == stageCid)
            {
                sawRelevant = true;
                AppendWithBoundarySpace(result, part.Prompt);
            }
        }

        if (sawRelevant)
        {
            return result.ToString().Trim();
        }
        return RemoveAllEditSections(canonical);
    }

    private static string CanonicalizeEditBrackets(
        string prompt,
        int stageIndex,
        int globalCid,
        int stageCid)
    {
        StringBuilder result = new(prompt.Length + 16);
        string[] pieces = prompt.Split('<');
        bool first = true;
        foreach (string piece in pieces)
        {
            if (first)
            {
                first = false;
                result.Append(piece);
                continue;
            }
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }
            int end = piece.IndexOf('>');
            if (end == -1)
            {
                result.Append('<').Append(piece);
                continue;
            }
            string tag = piece[..end];
            string content = piece[(end + 1)..];

            if (tag.Contains(EditCidMarker, StringComparison.OrdinalIgnoreCase)
                || !TryParseEditTag(tag, out string preData))
            {
                result.Append('<').Append(piece);
                continue;
            }

            int cid = ResolveBracketCid(preData, stageIndex, globalCid, stageCid);
            result.Append('<').Append(EditTagName).Append(EditCidMarker).Append(cid)
                  .Append('>').Append(content);
        }
        return result.ToString();
    }

    private static bool TryParseEditTag(string tag, out string preData)
    {
        preData = null;
        int colon = tag.IndexOf(':');
        string prefix = colon == -1 ? tag : tag[..colon];
        if (prefix.EndsWith(']') && prefix.Contains('['))
        {
            int open = prefix.LastIndexOf('[');
            preData = prefix[(open + 1)..^1];
            prefix = prefix[..open];
        }
        return StringUtils.Equals(prefix, EditTagName);
    }

    private static int ResolveBracketCid(
        string preData,
        int stageIndex,
        int globalCid,
        int stageCid)
    {
        if (string.IsNullOrWhiteSpace(preData))
        {
            return globalCid;
        }
        return int.TryParse(preData.Trim(), out int tagStage) && tagStage == stageIndex
            ? stageCid
            : NoMatchCid;
    }

    private static string RemoveAllEditSections(string canonicalPrompt)
    {
        if (string.IsNullOrWhiteSpace(canonicalPrompt))
        {
            return "";
        }
        if (!canonicalPrompt.Contains(EditOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return canonicalPrompt.Trim();
        }

        StringBuilder result = new();
        bool first = true;
        bool inEdit = false;
        foreach (string piece in canonicalPrompt.Split('<'))
        {
            if (first)
            {
                first = false;
                result.Append(piece);
                continue;
            }
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }
            int end = piece.IndexOf('>');
            if (end == -1)
            {
                if (!inEdit)
                {
                    result.Append('<').Append(piece);
                }
                continue;
            }
            string prefix = ExtractTagPrefixLower(piece[..end]);
            if (prefix == EditTagName)
            {
                inEdit = true;
                continue;
            }
            if (inEdit && !IsSectionStartingTag(prefix))
            {
                continue;
            }
            inEdit = false;
            result.Append('<').Append(piece);
        }
        return result.ToString().Trim();
    }

    private static string ExtractTagPrefixLower(string tag)
    {
        int colon = tag.IndexOf(':');
        string prefix = colon == -1 ? tag : tag[..colon];
        int slash = prefix.IndexOf('/');
        if (slash != -1)
        {
            prefix = prefix[..slash];
        }
        if (prefix.EndsWith(']') && prefix.Contains('['))
        {
            prefix = prefix[..prefix.LastIndexOf('[')];
        }
        return prefix.ToLowerInvariant();
    }

    private static void AppendWithBoundarySpace(StringBuilder dest, string add)
    {
        if (string.IsNullOrEmpty(add))
        {
            return;
        }
        if (dest.Length > 0
            && !char.IsWhiteSpace(dest[^1])
            && !char.IsWhiteSpace(add[0]))
        {
            dest.Append(' ');
        }
        dest.Append(add);
    }

    private static string ResolveB2EPromptTags(string sourcePrompt, string promptText, int stageIndex, HashSet<string> referenceStack)
    {
        if (string.IsNullOrWhiteSpace(promptText))
        {
            return "";
        }
        if (!promptText.Contains(B2EPromptOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return promptText.Trim();
        }

        StringBuilder resolved = new(promptText.Length);
        int cursor = 0;
        while (cursor < promptText.Length)
        {
            int open = promptText.IndexOf('<', cursor);
            if (open == -1)
            {
                resolved.Append(promptText[cursor..]);
                break;
            }

            if (open > cursor)
            {
                resolved.Append(promptText[cursor..open]);
            }

            int close = promptText.IndexOf('>', open + 1);
            if (close == -1)
            {
                resolved.Append(promptText[open..]);
                break;
            }

            string tag = promptText[(open + 1)..close];
            if (TryExtractTagPrefix(tag, out string prefixName, out string preData)
                && StringUtils.Equals(prefixName, B2EPromptTagName))
            {
                resolved.Append(ResolveB2EPromptReference(sourcePrompt, preData, stageIndex, referenceStack));
            }
            else
            {
                resolved.Append(promptText[open..(close + 1)]);
            }

            cursor = close + 1;
        }

        return resolved.ToString().Trim();
    }

    private static string ResolveB2EPromptReference(string sourcePrompt, string preData, int stageIndex, HashSet<string> referenceStack)
    {
        string target = string.IsNullOrWhiteSpace(preData) ? "global" : preData.Trim();
        string targetLower = target.ToLowerInvariant();

        if (targetLower is "global" or "base" or "refiner")
        {
            return ResolveNamedB2EPromptReference(sourcePrompt, targetLower, stageIndex, referenceStack);
        }

        if (int.TryParse(targetLower, out int targetStage) && targetStage >= 0)
        {
            string key = $"edit:{targetStage}";
            if (!referenceStack.Add(key))
            {
                Logs.Warning($"Base2Edit: Recursive <b2eprompt[{target}]> detected while resolving stage {stageIndex}; using global prompt fallback.");
                return GetGlobalPromptText(sourcePrompt);
            }

            try
            {
                if (!HasAnyEditSectionForStage(sourcePrompt, targetStage))
                {
                    return GetGlobalPromptText(sourcePrompt);
                }

                string stagePrompt = ExtractPromptWithoutB2EPrompt(sourcePrompt, targetStage);
                return ResolveB2EPromptTags(sourcePrompt, stagePrompt, targetStage, referenceStack);
            }
            finally
            {
                referenceStack.Remove(key);
            }
        }

        throw new SwarmUserErrorException(
            $"Base2Edit: Invalid <b2eprompt[{target}]> in stage {stageIndex}: unrecognized target. "
            + "Expected [global], [base], [refiner], or a non-negative stage index.");
    }

    private static string ResolveNamedB2EPromptReference(string sourcePrompt, string targetLower, int stageIndex, HashSet<string> referenceStack)
    {
        string key = $"named:{targetLower}";
        if (!referenceStack.Add(key))
        {
            Logs.Warning($"Base2Edit: Recursive <b2eprompt[{targetLower}]> detected while resolving stage {stageIndex}; using global prompt fallback.");
            return GetGlobalPromptText(sourcePrompt);
        }

        try
        {
            PromptRegion region = new(sourcePrompt ?? "");
            string selected = targetLower switch
            {
                "base" => string.IsNullOrWhiteSpace(region.BasePrompt) ? region.GlobalPrompt : region.BasePrompt,
                "refiner" => string.IsNullOrWhiteSpace(region.RefinerPrompt) ? region.GlobalPrompt : region.RefinerPrompt,
                _ => region.GlobalPrompt
            };
            return ResolveB2EPromptTags(sourcePrompt, selected, stageIndex, referenceStack);
        }
        finally
        {
            referenceStack.Remove(key);
        }
    }

    private static string GetGlobalPromptText(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "";
        }

        PromptRegion region = new(prompt);
        return region.GlobalPrompt.Trim();
    }
}
