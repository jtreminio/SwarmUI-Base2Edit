namespace Base2Edit;

public static class PathSanitizer
{
    private const int MaxLength = 80;

    public static string Sanitize(string videoFileName)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(videoFileName ?? "");
        System.Text.StringBuilder sb = new();
        foreach (char c in name)
        {
            sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'
                      ? c : '_');
        }
        string result = sb.ToString().Trim('_');
        if (result.Length == 0)
        {
            return null;
        }
        if (result.Length > MaxLength)
        {
            result = result[..MaxLength];
        }
        if (result.Contains(".."))
        {
            return null;
        }
        return result;
    }
}
