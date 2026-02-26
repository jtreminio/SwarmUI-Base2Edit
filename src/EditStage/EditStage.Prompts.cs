using System.Text;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Base2Edit;

public partial class EditStage
{
    private static bool HasAnyEditSectionForStage(string prompt, int stageIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int globalCid = Base2EditExtension.SectionID_Edit;
        int stageCid = Base2EditExtension.EditSectionIdForStage(stageIndex);

        foreach (string piece in prompt.Split('<'))
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                continue;
            }

            string tag = piece[..end];

            string prefixPart = tag;
            int colon = tag.IndexOf(':');
            if (colon != -1)
            {
                prefixPart = tag[..colon];
            }
            prefixPart = prefixPart.Split('/')[0];

            string prefixName = prefixPart;
            string preData = null;
            if (prefixName.EndsWith(']') && prefixName.Contains('['))
            {
                int open = prefixName.LastIndexOf('[');
                if (open != -1)
                {
                    preData = prefixName[(open + 1)..^1];
                    prefixName = prefixName[..open];
                }
            }

            if (!string.Equals(prefixName, "edit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int cidCut = tag.LastIndexOf("//cid=", StringComparison.OrdinalIgnoreCase);
            if (cidCut != -1 && int.TryParse(tag[(cidCut + "//cid=".Length)..], out int cid))
            {
                if (cid == globalCid || cid == stageCid)
                {
                    return true;
                }
                continue;
            }

            if (preData is null)
            {
                return true;
            }
            if (int.TryParse(preData, out int tagStage) && tagStage == stageIndex)
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractPrompt(string prompt, string originalPrompt, int stageIndex)
    {
        string extracted = ExtractPromptWithoutB2EPrompt(prompt, stageIndex);
        string resolved = ResolveB2EPromptTags(prompt, extracted, stageIndex, []);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        if (!ShouldFallbackForTagOnlyEditSection(prompt, originalPrompt, stageIndex))
        {
            return (resolved ?? "").Trim();
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

    private static string GetOriginalPrompt(T2IParamInput input, string paramId, string fallback)
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

        if (sourceSection.Contains("<b2eimage", StringComparison.OrdinalIgnoreCase))
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
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('<'))
        {
            return text ?? "";
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

        if (!prompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
        {
            return prompt.Trim();
        }

        HashSet<string> sectionEndingTags = ["base", "refiner", "video", "videoswap", "region", "segment", "object"];
        int globalCid = Base2EditExtension.SectionID_Edit;
        int stageCid = Base2EditExtension.EditSectionIdForStage(stageIndex);

        static void AppendWithBoundarySpace(ref string dest, string add)
        {
            if (string.IsNullOrEmpty(add))
            {
                return;
            }
            if (!string.IsNullOrEmpty(dest)
                && !char.IsWhiteSpace(dest[^1])
                && !char.IsWhiteSpace(add[0]))
            {
                dest += " ";
            }
            dest += add;
        }

        static string RemoveAllEditSections(string fullPrompt, HashSet<string> sectionEndingTagsLocal)
        {
            if (string.IsNullOrWhiteSpace(fullPrompt) || !fullPrompt.Contains("<edit", StringComparison.OrdinalIgnoreCase))
            {
                return (fullPrompt ?? "").Trim();
            }

            string resultLocal = "";
            bool inAnyEditSection = false;
            string[] piecesLocal = fullPrompt.Split('<');
            bool isFirstPiece = true;

            foreach (string piece in piecesLocal)
            {
                if (isFirstPiece)
                {
                    isFirstPiece = false;
                    if (!inAnyEditSection)
                    {
                        resultLocal += piece;
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(piece))
                {
                    continue;
                }

                int end = piece.IndexOf('>');
                if (end == -1)
                {
                    if (!inAnyEditSection)
                    {
                        resultLocal += "<" + piece;
                    }
                    continue;
                }

                string tag = piece[..end];

                if (!TryExtractTagPrefix(tag, out string prefixName, out _))
                {
                    if (!inAnyEditSection)
                    {
                        resultLocal += "<" + piece;
                    }
                    continue;
                }

                string tagPrefixLower = prefixName.ToLowerInvariant();
                bool isEditTag = tagPrefixLower == "edit";

                if (isEditTag)
                {
                    inAnyEditSection = true;
                    continue;
                }

                if (inAnyEditSection)
                {
                    if (sectionEndingTagsLocal.Contains(tagPrefixLower))
                    {
                        inAnyEditSection = false;
                    }
                    else
                    {
                        continue;
                    }
                }

                resultLocal += "<" + piece;
            }

            return resultLocal.Trim();
        }

        string result = "";
        string[] pieces = prompt.Split('<');
        bool inWantedSection = false;
        bool sawRelevantEditTag = false;

        foreach (string piece in pieces)
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                if (inWantedSection)
                {
                    result += "<" + piece;
                }
                continue;
            }

            string tag = piece[..end];
            string content = piece[(end + 1)..];

            if (!TryExtractTagPrefix(tag, out string prefixName, out string preData))
            {
                if (inWantedSection)
                {
                    result += "<" + piece;
                }
                continue;
            }

            string tagPrefixLower = prefixName.ToLowerInvariant();
            bool isEditTag = tagPrefixLower == "edit";
            if (isEditTag)
            {
                bool wantThisSection = false;

                int cidCut = tag.LastIndexOf("//cid=", StringComparison.OrdinalIgnoreCase);
                if (cidCut != -1 && int.TryParse(tag[(cidCut + "//cid=".Length)..], out int cid))
                {
                    wantThisSection = cid == globalCid || cid == stageCid;
                }
                else if (preData is null)
                {
                    wantThisSection = true;
                }
                else if (int.TryParse(preData, out int tagStage) && tagStage == stageIndex)
                {
                    wantThisSection = true;
                }

                if (wantThisSection)
                {
                    sawRelevantEditTag = true;
                }

                inWantedSection = wantThisSection;
                if (inWantedSection)
                {
                    AppendWithBoundarySpace(ref result, content);
                }
            }
            else if (inWantedSection)
            {
                if (sectionEndingTags.Contains(tagPrefixLower))
                {
                    inWantedSection = false;
                }
                else
                {
                    result += "<" + piece;
                }
            }
        }

        if (!sawRelevantEditTag)
        {
            return RemoveAllEditSections(prompt, sectionEndingTags);
        }

        return result.Trim();
    }

    private static string ResolveB2EPromptTags(string sourcePrompt, string promptText, int stageIndex, HashSet<string> referenceStack)
    {
        if (string.IsNullOrWhiteSpace(promptText) || !promptText.Contains("<b2eprompt", StringComparison.OrdinalIgnoreCase))
        {
            return (promptText ?? "").Trim();
        }

        string resolved = "";
        int cursor = 0;
        while (cursor < promptText.Length)
        {
            int open = promptText.IndexOf('<', cursor);
            if (open == -1)
            {
                resolved += promptText[cursor..];
                break;
            }

            if (open > cursor)
            {
                resolved += promptText[cursor..open];
            }

            int close = promptText.IndexOf('>', open + 1);
            if (close == -1)
            {
                resolved += promptText[open..];
                break;
            }

            string tag = promptText[(open + 1)..close];
            if (TryExtractTagPrefix(tag, out string prefixName, out string preData)
                && string.Equals(prefixName, "b2eprompt", StringComparison.OrdinalIgnoreCase))
            {
                resolved += ResolveB2EPromptReference(sourcePrompt, preData, stageIndex, referenceStack);
            }
            else
            {
                resolved += promptText[open..(close + 1)];
            }

            cursor = close + 1;
        }

        return resolved.Trim();
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

        Logs.Warning($"Base2Edit: Invalid <b2eprompt[{target}]> in stage {stageIndex}; using global prompt fallback.");
        return GetGlobalPromptText(sourcePrompt);
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
}
