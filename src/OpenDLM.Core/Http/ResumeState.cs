using System.Text.Json;
using System.Text.Json.Serialization;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Http;

/// <summary>
/// The on-disk resume sidecar written next to a partial file
/// (<c>&lt;file&gt;.opendlm</c>). It records where every segment stopped and the
/// validators needed to prove the remote file has not changed since.
/// </summary>
public sealed class ResumeState
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("finalUrl")]
    public string? FinalUrl { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; } = -1;

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("lastModified")]
    public string? LastModified { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("connections")]
    public int Connections { get; set; } = 1;

    [JsonPropertyName("segments")]
    public List<Segment> Segments { get; set; } = new();

    [JsonPropertyName("savedAt")]
    public DateTime SavedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Reads, validates and writes resume sidecars.
///
/// Validation is deliberately strict: if the size or either validator changed, the
/// partial file is discarded and the download restarts. Resuming onto a changed
/// resource would silently produce a corrupt file, which is far worse than
/// re-downloading.
/// </summary>
public static class ResumeStateStore
{
    public static void Save(DownloadItem item, ResumeState state)
    {
        try
        {
            state.SavedAt = DateTime.Now;
            state.Segments = item.Segments.Select(Clone).ToList();
            state.TotalBytes = item.TotalBytes;
            state.Url = item.Url;
            state.FinalUrl = item.FinalUrl;
            state.UserAgent = item.UserAgent;

            SettingsService.AtomicWrite(item.ResumePath,
                JsonSerializer.Serialize(state, SettingsService.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write resume state for '{item.FileName}': {ex.Message}");
        }
    }

    public static ResumeState? Load(DownloadItem item)
    {
        try
        {
            if (!File.Exists(item.ResumePath))
            {
                return null;
            }
            var json = File.ReadAllText(item.ResumePath);
            return JsonSerializer.Deserialize<ResumeState>(json, SettingsService.JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"Unreadable resume state for '{item.FileName}': {ex.Message}");
            return null;
        }
    }

    public static void Delete(DownloadItem item)
    {
        TryDelete(item.ResumePath);
    }

    public static void DeletePartialFile(DownloadItem item)
    {
        TryDelete(item.PartialPath);
        TryDelete(item.PartialPath + ".tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Decides whether an existing partial file may be continued.
    /// Returns the segments to continue with, or <c>null</c> to start over.
    /// </summary>
    public static List<Segment>? TryResume(DownloadItem item, ProbeResult probe)
    {
        var state = Load(item);
        if (state is null)
        {
            return null;
        }

        if (!File.Exists(item.PartialPath))
        {
            Log.Info($"No partial file for '{item.FileName}'; starting from scratch.");
            return null;
        }

        if (!UriEquals(state.Url, item.Url))
        {
            Log.Info($"Resume rejected for '{item.FileName}': the URL changed.");
            return null;
        }

        // Size must agree when the server reports one.
        if (probe.ContentLength > 0 && state.TotalBytes > 0 && probe.ContentLength != state.TotalBytes)
        {
            Log.Info($"Resume rejected for '{item.FileName}': size changed " +
                     $"({state.TotalBytes} -> {probe.ContentLength}).");
            return null;
        }

        // Prefer the strong validator, fall back to Last-Modified.
        if (!string.IsNullOrEmpty(probe.ETag) && !string.IsNullOrEmpty(state.ETag) &&
            !string.Equals(probe.ETag, state.ETag, StringComparison.Ordinal))
        {
            Log.Info($"Resume rejected for '{item.FileName}': ETag changed.");
            return null;
        }

        if (!string.IsNullOrEmpty(probe.LastModified) && !string.IsNullOrEmpty(state.LastModified) &&
            !string.Equals(probe.LastModified, state.LastModified, StringComparison.Ordinal))
        {
            Log.Info($"Resume rejected for '{item.FileName}': Last-Modified changed.");
            return null;
        }

        if (state.Segments.Count == 0)
        {
            return null;
        }

        long fileLength;
        try
        {
            fileLength = new FileInfo(item.PartialPath).Length;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not stat the partial file: " + ex.Message);
            return null;
        }

        var total = probe.ContentLength > 0 ? probe.ContentLength : state.TotalBytes;

        // The partial file must be at least as large as the furthest byte written;
        // a smaller file means the sidecar and the data are out of sync.
        var furthest = state.Segments.Max(s => s.Position);
        if (fileLength < furthest)
        {
            Log.Info($"Resume rejected for '{item.FileName}': the partial file is smaller than the resume state.");
            return null;
        }

        var segments = state.Segments.Select(Clone).ToList();

        // Re-aim the last segment if the server now reports a different end.
        if (total > 0)
        {
            var last = segments[^1];
            if (last.End != total - 1)
            {
                last.End = total - 1;
                if (last.Position > last.End)
                {
                    last.Position = last.Start;
                }
            }
        }

        if (segments.All(s => s.IsComplete))
        {
            Log.Info($"Resume state for '{item.FileName}' is already complete; finalizing instead.");
        }

        Log.Info($"Resuming '{item.FileName}' at " +
                 $"{segments.Sum(s => s.BytesWritten) / 1024 / 1024} MB from {segments.Count} segment(s).");

        return segments;
    }

    private static Segment Clone(Segment segment) => new()
    {
        Index = segment.Index,
        Start = segment.Start,
        End = segment.End,
        Position = segment.Position
    };

    private static bool UriEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        // Treat http/https and default-port differences as the same resource.
        if (Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub))
        {
            return Uri.Compare(ua, ub, UriComponents.HttpRequestUrl, UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) == 0;
        }

        return false;
    }
}
