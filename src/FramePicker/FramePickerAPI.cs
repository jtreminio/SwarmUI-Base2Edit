using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.IO;

namespace Base2Edit;

[API.APIClass("Frame Picker API — extract and save individual frames from video outputs.")]
public static class FramePickerAPI
{
    public static void Register()
    {
        API.RegisterAPICall(B2EFramePickerOpen, false, Permissions.ViewImageHistory);
        API.RegisterAPICall(B2EFramePickerSave, false, Permissions.ViewImageHistory);
        API.RegisterAPICall(B2EFramePickerListSaved, false, Permissions.ViewImageHistory);
    }

    private static string GetOutputRoot(Session session)
        => Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, session.User.OutputDirectory);

    private static string ResolveVideoPath(Session session, string videoUrl)
    {
        string root = GetOutputRoot(session);
        string rel = videoUrl.Replace('\\', '/');
        int q = rel.IndexOf('?');
        if (q >= 0)
        {
            rel = rel[..q];
        }
        rel = rel.TrimStart('/');
        if (rel.StartsWith("Output/", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel["Output/".Length..];
        }
        else if (rel.StartsWith("View/", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel["View/".Length..];
            int slash = rel.IndexOf('/');
            if (slash >= 0)
            {
                rel = rel[(slash + 1)..];
            }
        }
        (string path, string consoleError, _) = WebServer.CheckFilePath(root, rel);
        if (consoleError is not null)
        {
            return null;
        }
        path = UserImageHistoryHelper.GetRealPathFor(session.User, path, root: root);
        if (!File.Exists(path))
        {
            return null;
        }
        return path;
    }

    private static string ThumbCacheDir(Session session, string sanitizedKey)
        => Path.GetFullPath(Path.Combine(GetOutputRoot(session), "raw", ".frame-picker-cache", sanitizedKey));

    private static string SavedFramesDir(Session session, string sanitizedKey)
        => Path.GetFullPath(Path.Combine(GetOutputRoot(session), "inputs", "frame-picker", sanitizedKey));

    private static string ThumbUrlPattern(Session session, string sanitizedKey)
    {
        string prefix = Program.ServerSettings.Paths.AppendUserNameToOutputPath
            ? $"View/{session.User.UserID}"
            : "Output";
        return $"/{prefix}/raw/.frame-picker-cache/{sanitizedKey}/thumb_NNN.jpg";
    }

    private static List<int> ReadSavedIndices(string savedDir)
    {
        if (!Directory.Exists(savedDir))
        {
            return [];
        }
        List<int> indices = [];
        foreach (string f in Directory.EnumerateFiles(savedDir, "frame_*.png"))
        {
            string numPart = Path.GetFileNameWithoutExtension(f).After("frame_");
            if (int.TryParse(numPart, out int n))
            {
                indices.Add(n - 1);
            }
        }
        indices.Sort();
        return indices;
    }


    [API.APIDescription(
        "Open the Frame Picker for a video: probe metadata, extract frames into the thumb cache if needed, return state.",
        """
        {
          "frameCount": 144,
          "fps": 24.0,
          "width": 1920,
          "height": 1080,
          "thumbUrlPattern": "/View/{userId}/raw/.frame-picker-cache/{key}/thumb_NNN.jpg",
          "savedSelection": [11, 53, 95]
        }
        """)]
    public static async Task<JObject> B2EFramePickerOpen(
        Session session,
        [API.APIParameter("The /View/ or /Output/ URL of the video.")] string videoUrl)
    {
        string videoPath = ResolveVideoPath(session, videoUrl);
        if (videoPath is null)
        {
            return new JObject() { ["error"] = "Video file not found or path is invalid." };
        }
        string sanitizedKey = PathSanitizer.Sanitize(Path.GetFileName(videoPath));
        if (sanitizedKey is null)
        {
            return new JObject() { ["error"] = "Cannot sanitize video filename." };
        }
        string cacheDir = ThumbCacheDir(session, sanitizedKey);
        string savedDir = SavedFramesDir(session, sanitizedKey);
        VideoMetadata meta = await FrameExtractor.ProbeVideoMetadata(videoPath);
        if (meta is null)
        {
            return new JObject() { ["error"] = "Could not probe video metadata. Is ffmpeg installed?" };
        }
        bool cacheExists = Directory.Exists(cacheDir) &&
            Directory.EnumerateFiles(cacheDir, "frame_*.png").Any();
        if (!cacheExists)
        {
            bool ok = await FrameExtractor.ExtractAllFrames(videoPath, cacheDir);
            if (!ok)
            {
                return new JObject() { ["error"] = "Frame extraction failed. Is ffmpeg installed?" };
            }
        }
        int actualFrameCount = Directory.EnumerateFiles(cacheDir, "frame_*.png").Count();
        if (actualFrameCount <= 0)
        {
            return new JObject() { ["error"] = "Frame extraction produced no files." };
        }
        List<int> saved = ReadSavedIndices(savedDir);
        return new JObject()
        {
            ["frameCount"] = actualFrameCount,
            ["fps"] = meta.Fps,
            ["width"] = meta.Width,
            ["height"] = meta.Height,
            ["thumbUrlPattern"] = ThumbUrlPattern(session, sanitizedKey),
            ["savedSelection"] = new JArray(saved)
        };
    }

    [API.APIDescription(
        "Save a frame selection: reconcile inputs/frame-picker/{key}/ to the new selection set.",
        """{ "added": [0, 2], "removed": [1] }""")]
    public static async Task<JObject> B2EFramePickerSave(
        Session session,
        [API.APIParameter("The /View/ or /Output/ URL of the video.")] string videoUrl,
        [API.APIParameter("The full input body containing videoUrl + frameIndices (0-based int array).")] JObject raw)
    {
        JArray frameIndices = raw["frameIndices"] as JArray ?? [];
        string videoPath = ResolveVideoPath(session, videoUrl);
        if (videoPath is null)
        {
            return new JObject()
            {
                ["error"] = "Video file not found."
            };
        }
        string sanitizedKey = PathSanitizer.Sanitize(Path.GetFileName(videoPath));
        if (sanitizedKey is null)
        {
            return new JObject()
            {
                ["error"] = "Cannot sanitize filename."
            };
        }
        string cacheDir = ThumbCacheDir(session, sanitizedKey);
        string savedDir = SavedFramesDir(session, sanitizedKey);
        HashSet<int> requested = [];
        foreach (JToken tok in frameIndices)
        {
            if (tok.Value<int>() is int i)
            {
                requested.Add(i);
            }
        }
        List<int> current = ReadSavedIndices(savedDir);
        HashSet<int> currentSet = [.. current];
        List<int> added = [], removed = [];
        foreach (int idx in currentSet)
        {
            if (!requested.Contains(idx))
            {
                string fname = $"frame_{idx + 1:D6}.png";
                string target = Path.Combine(savedDir, fname);
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
                removed.Add(idx);
            }
        }
        Directory.CreateDirectory(savedDir);
        foreach (int idx in requested)
        {
            if (!currentSet.Contains(idx))
            {
                string src = Path.Combine(cacheDir, $"frame_{idx + 1:D6}.png");
                string dest = Path.Combine(savedDir, $"frame_{idx + 1:D6}.png");
                if (!File.Exists(src))
                {
                    continue;
                }
                File.Copy(src, dest, overwrite: true);
                added.Add(idx);
            }
        }
        return new JObject()
        {
            ["added"] = new JArray(added),
            ["removed"] = new JArray(removed)
        };
    }

    [API.APIDescription(
        "List currently saved frame indices for a video (cheap read-only call).",
        """{ "savedSelection": [11, 53, 95] }""")]
    public static async Task<JObject> B2EFramePickerListSaved(
        Session session,
        [API.APIParameter("The /View/ or /Output/ URL of the video.")] string videoUrl)
    {
        string videoPath = ResolveVideoPath(session, videoUrl);
        if (videoPath is null)
        {
            return new JObject()
            {
                ["error"] = "Video file not found."
            };
        }
        string sanitizedKey = PathSanitizer.Sanitize(Path.GetFileName(videoPath));
        if (sanitizedKey is null)
        {
            return new JObject()
            {
                ["error"] = "Cannot sanitize filename."
            };
        }
        List<int> saved = ReadSavedIndices(SavedFramesDir(session, sanitizedKey));
        return new JObject()
        {
            ["savedSelection"] = new JArray(saved)
        };
    }
}
