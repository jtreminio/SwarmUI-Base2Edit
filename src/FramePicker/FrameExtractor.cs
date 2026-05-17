using FreneticUtilities.FreneticToolkit;
using SwarmUI.Accounts;
using SwarmUI.Utils;
using System.IO;
using System.Text.RegularExpressions;

namespace Base2Edit;

public static partial class FrameExtractor
{
    public static async Task<VideoMetadata> ProbeVideoMetadata(string videoPath)
    {
        string ffmpeg = Utilities.FfmegLocation.Value;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(videoPath))
        {
            return null;
        }
        string output = await Utilities.QuickRunProcess(ffmpeg, ["-i", videoPath, "-hide_banner"]);
        return ParseFfmpegProbeOutput(output);
    }

    private static VideoMetadata ParseFfmpegProbeOutput(string output)
    {
        int width = 0, height = 0;
        double fps = 0;
        double totalSeconds = 0;

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Stream") && trimmed.Contains("Video:"))
            {
                Match resMatch = WidthHeightRegex().Match(trimmed);
                if (resMatch.Success)
                {
                    width = int.Parse(resMatch.Groups[1].Value);
                    height = int.Parse(resMatch.Groups[2].Value);
                }
                Match fpsMatch = FpsRegex().Match(trimmed);
                if (!fpsMatch.Success)
                {
                    fpsMatch = TbrRegex().Match(trimmed);
                }
                if (fpsMatch.Success)
                {
                    fps = double.Parse(fpsMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            if (trimmed.StartsWith("Duration:"))
            {
                Match durationMatch = DurationRegex().Match(trimmed);
                if (durationMatch.Success)
                {
                    int hours = int.Parse(durationMatch.Groups[1].Value);
                    int minutes = int.Parse(durationMatch.Groups[2].Value);
                    double seconds = double.Parse(durationMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    totalSeconds = hours * 3600 + minutes * 60 + seconds;
                }
            }
        }

        if (fps <= 0 || width <= 0 || height <= 0 || totalSeconds <= 0)
        {
            return null;
        }
        int frameCount = (int)Math.Round(totalSeconds * fps);
        if (frameCount <= 0)
        {
            return null;
        }
        return new VideoMetadata(frameCount, fps, width, height);
    }

    public static async Task<bool> ExtractAllFrames(string videoPath, string thumbCacheDir)
    {
        string ffmpeg = Utilities.FfmegLocation.Value;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            return false;
        }
        Directory.CreateDirectory(thumbCacheDir);
        using ManyReadOneWriteLock.WriteClaim claim = UserImageHistoryHelper.FfmpegLock.LockWrite();
        await Utilities.QuickRunProcess(ffmpeg,
        [
            "-i", videoPath,
            "-vsync", "0",
            "-f", "image2",
            Path.Combine(thumbCacheDir, "frame_%06d.png")
        ]);
        await Utilities.QuickRunProcess(ffmpeg,
        [
            "-i", videoPath,
            "-vsync", "0",
            "-vf", "scale=160:-2",
            "-q:v", "5",
            "-f", "image2",
            Path.Combine(thumbCacheDir, "thumb_%06d.jpg")
        ]);
        return true;
    }

    [GeneratedRegex(@"(\d{2,5})x(\d{2,5})")]
    private static partial Regex WidthHeightRegex();
    [GeneratedRegex(@"([\d.]+)\s*fps")]
    private static partial Regex FpsRegex();
    [GeneratedRegex(@"([\d.]+)\s*tbr")]
    private static partial Regex TbrRegex();
    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();
}

public record VideoMetadata(int FrameCount, double Fps, int Width, int Height);
