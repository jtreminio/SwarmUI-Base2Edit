using System;
using System.Collections.Generic;
using SwarmUI.Text2Image;

namespace Base2Edit;

public static class LoraParsing
{
    // Parses "<lora:name[:strength[:tencStrength]]>" tags out of a prompt string
    public static (List<string> Loras, List<string> Weights, List<string> TencWeights) ParseEditPromptLoras(
        string combined,
        IEnumerable<string> available
    ) {
        if (string.IsNullOrWhiteSpace(combined)) {
            return ([], [], []);
        }
        available ??= Array.Empty<string>();
        string lower = combined.ToLowerInvariant();
        if (!lower.Contains("<lora:"))
        {
            return ([], [], []);
        }

        List<string> lorasOut = [];
        List<string> weightsOut = [];
        List<string> tencWeightsOut = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        int idx = 0;
        while (idx < lower.Length)
        {
            int start = lower.IndexOf("<lora:", idx, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            int end = lower.IndexOf('>', start);
            if (end < 0)
            {
                break;
            }

            string raw = combined[(start + "<lora:".Length)..end].Trim();
            idx = end + 1;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string loraName = raw.Replace('\\', '/').ToLowerInvariant();
            double strength = 1;
            double tencStrength = double.NaN;
            int colonIndex = loraName.IndexOf(':');
            if (colonIndex != -1)
            {
                string after = loraName[(colonIndex + 1)..];
                loraName = loraName[..colonIndex];
                colonIndex = after.IndexOf(':');
                if (colonIndex != -1)
                {
                    if (double.TryParse(after[..colonIndex], out double s))
                    {
                        strength = s;
                    }

                    if (double.TryParse(after[(colonIndex + 1)..], out double ts))
                    {
                        tencStrength = ts;
                    }
                }
                else if (double.TryParse(after, out double s))
                {
                    strength = s;
                }
            }

            string matched = T2IParamTypes.GetBestModelInList(loraName, available);
            if (matched is null)
            {
                continue;
            }

            if (matched.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            {
                int dot = matched.LastIndexOf('.');
                if (dot > 0)
                {
                    matched = matched[..dot];
                }
            }

            string clean = T2IParamTypes.CleanModelName(matched);
            if (!seen.Add(clean))
            {
                continue;
            }

            lorasOut.Add(matched);
            weightsOut.Add(strength.ToString());
            tencWeightsOut.Add((double.IsNaN(tencStrength) ? strength : tencStrength).ToString());
        }

        return (lorasOut, weightsOut, tencWeightsOut);
    }
}

