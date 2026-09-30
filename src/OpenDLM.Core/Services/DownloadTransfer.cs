using System.Text;
using System.Text.Json;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>
/// Moves the download list in and out of a file.
///
/// Two formats are supported, mirroring the reference tool: a rich OpenDLM file
/// that keeps every setting so an export can be re-imported without loss, and a
/// plain text file of one address per line that any other tool can read. Export also
/// offers a tab-separated text form so the columns survive a spreadsheet.
/// </summary>
public static class DownloadTransfer
{
    /// <summary>Magic line that marks a rich export, so the format is self-identifying.</summary>
    private const string Header = "# OpenDLM export";

    private const string Version = "1";

    /// <summary>Writes the list to a rich export file.</summary>
    public static void Export(string path, IEnumerable<DownloadItem> items)
    {
        var payload = new ExportFile
        {
            Version = Version,
            ExportedAt = DateTimeOffset.Now,
            Items = items.Select(ToRecord).ToList()
        };

        var json = JsonSerializer.Serialize(payload, SettingsService.JsonOptions);
        SettingsService.AtomicWrite(path, Header + Environment.NewLine + json);
    }

    /// <summary>Writes one address per line, for tools that do not read our format.</summary>
    public static void ExportUrls(string path, IEnumerable<DownloadItem> items)
    {
        var builder = new StringBuilder();

        foreach (var item in items)
        {
            builder.AppendLine(item.Url);

            if (!string.IsNullOrWhiteSpace(item.Referer) &&
                !string.Equals(item.Referer, item.Url, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('\t').AppendLine(item.Referer);
            }
        }

        SettingsService.AtomicWrite(path, builder.ToString());
    }

    /// <summary>
    /// Reads either format back. Returns the imports and how many lines were
    /// skipped, so the caller can tell the user rather than silently dropping them.
    /// </summary>
    public static ImportResult Import(string path, Func<string, AddDownloadRequest> buildRequest)
    {
        var text = File.ReadAllText(path);

        return text.Contains(Header, StringComparison.OrdinalIgnoreCase)
            ? ImportRich(text, buildRequest)
            : ImportUrls(text, buildRequest);
    }

    private static ImportResult ImportRich(string text, Func<string, AddDownloadRequest> buildRequest)
    {
        var json = text.Substring(text.IndexOf('\n') + 1);
        var payload = JsonSerializer.Deserialize<ExportFile>(json, SettingsService.JsonOptions);

        if (payload?.Items is not { Count: > 0 })
        {
            return new ImportResult(Array.Empty<AddDownloadRequest>(), 0);
        }

        var requests = new List<AddDownloadRequest>();
        var skipped = 0;

        foreach (var record in payload.Items)
        {
            if (string.IsNullOrWhiteSpace(record.Url))
            {
                skipped++;
                continue;
            }

            requests.Add(FromRecord(record, buildRequest));
        }

        return new ImportResult(requests, skipped);
    }

    private static ImportResult ImportUrls(string text, Func<string, AddDownloadRequest> buildRequest)
    {
        var requests = new List<AddDownloadRequest>();
        var skipped = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // A line may be "url<TAB>referer".
            var parts = line.Split('\t', 2);
            var url = parts[0].Trim();

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                skipped++;
                continue;
            }

            var request = buildRequest(url);
            if (parts.Length > 1)
            {
                request.Referer = parts[1].Trim();
            }

            requests.Add(request);
        }

        return new ImportResult(requests, skipped);
    }

    private static ExportRecord ToRecord(DownloadItem item) => new()
    {
        Url = item.Url,
        FileName = item.FileName,
        Directory = item.Directory,
        Description = item.Description,
        Referer = item.Referer,
        PageUrl = item.PageUrl,
        Cookies = item.Cookies,
        UserAgent = item.UserAgent,
        Username = item.Username,
        Password = item.Password,
        Category = item.Category,
        QueueId = item.QueueId,
        Connections = item.Connections,
        TotalBytes = item.TotalBytes,
        Status = item.Status
    };

    private static AddDownloadRequest FromRecord(ExportRecord record, Func<string, AddDownloadRequest> buildRequest)
    {
        var request = buildRequest(record.Url);

        request.FileName = record.FileName;
        request.Directory = record.Directory;
        request.Description = record.Description;
        request.Referer = record.Referer;
        request.PageUrl = record.PageUrl;
        request.Cookies = record.Cookies;
        request.UserAgent = record.UserAgent;
        request.Username = record.Username;
        request.Password = record.Password;
        request.Category = record.Category;
        request.QueueId = record.QueueId;
        request.Connections = record.Connections;
        request.StartNow = record.Status == DownloadStatus.Downloading;
        request.AddToQueue = record.Status != DownloadStatus.Complete;

        return request;
    }

    public sealed class ExportFile
    {
        public string Version { get; set; } = "1";
        public DateTimeOffset ExportedAt { get; set; }
        public List<ExportRecord> Items { get; set; } = new();
    }

    /// <summary>What an export keeps, so a re-import restores the same shape.</summary>
    public sealed class ExportRecord
    {
        public string Url { get; set; } = string.Empty;
        public string? FileName { get; set; }
        public string? Directory { get; set; }
        public string? Description { get; set; }
        public string? Referer { get; set; }
        public string? PageUrl { get; set; }
        public string? Cookies { get; set; }
        public string? UserAgent { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public DownloadCategory Category { get; set; }
        public int QueueId { get; set; }
        public int Connections { get; set; } = 1;
        public long TotalBytes { get; set; } = -1;
        public DownloadStatus Status { get; set; }
    }

    public sealed record ImportResult(IReadOnlyList<AddDownloadRequest> Requests, int SkippedLines);
}
