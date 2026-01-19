namespace Base2Edit;

public static class EditPromptParser
{
    private static readonly HashSet<string> SectionEndingTags =
        ["base", "refiner", "video", "videoswap", "region", "segment", "object"];

    public static string Extract(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("<edit"))
        {
            return "";
        }

        string result = "";
        string[] pieces = prompt.Split('<');
        bool inEditSection = false;

        foreach (string piece in pieces)
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            int end = piece.IndexOf('>');
            if (end == -1)
            {
                if (inEditSection)
                {
                    result += "<" + piece;
                }
                continue;
            }

            string tag = piece[..end];
            // Handle <edit>, <edit:data>, and <edit//cid=X> formats
            string tagPrefix = tag.Split(':')[0].Split('/')[0];
            string content = piece[(end + 1)..];

            if (tagPrefix == "edit")
            {
                inEditSection = true;
                result += content;
            }
            else if (inEditSection)
            {
                if (SectionEndingTags.Contains(tagPrefix))
                {
                    break;
                }
                else
                {
                    result += "<" + piece;
                }
            }
        }

        return result.Trim();
    }

    public static bool HasEditSection(string prompt)
    {
        return !string.IsNullOrWhiteSpace(Extract(prompt));
    }
}
