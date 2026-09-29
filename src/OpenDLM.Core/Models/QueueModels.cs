using System.Text.Json.Serialization;

namespace OpenDLM.Core.Models;

/// <summary>
/// A named queue. Queues let the user batch downloads and run them at a scheduled
/// time, and they are the unit the scheduler starts and stops.
/// </summary>
public sealed class DownloadQueue
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Main queue";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>When true the queue only runs inside its start/stop window.</summary>
    [JsonPropertyName("scheduled")]
    public bool Scheduled { get; set; }

    [JsonPropertyName("startTime")]
    public TimeSpan StartTime { get; set; } = new(9, 0, 0);

    [JsonPropertyName("stopTime")]
    public TimeSpan StopTime { get; set; } = new(17, 0, 0);

    /// <summary>Bit mask, bit 0 = Sunday .. bit 6 = Saturday. 0x7F means every day.</summary>
    [JsonPropertyName("days")]
    public int Days { get; set; } = 0x7F;

    /// <summary>Maximum number of files from this queue that may run at the same time.</summary>
    [JsonPropertyName("maxConcurrent")]
    public int MaxConcurrent { get; set; } = 1;

    [JsonIgnore]
    public bool IsDefault => Id == 0;

    public bool RunsOn(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

    public DownloadQueue Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Scheduled = Scheduled,
        StartTime = StartTime,
        StopTime = StopTime,
        Days = Days,
        MaxConcurrent = MaxConcurrent
    };
}

/// <summary>
/// A stored credential for a protected server (an HTTP authentication realm),
/// mirroring the "site logins" feature of commercial download managers.
/// </summary>
public sealed class SiteLogin
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Host or host:port, optionally with a path prefix, e.g. "files.example.com".</summary>
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    /// <summary>Optional realm/description the server advertised.</summary>
    [JsonPropertyName("realm")]
    public string? Realm { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Ciphertext produced by <c>CredentialProtector</c>; empty when the entry has no password.</summary>
    [JsonPropertyName("passwordProtected")]
    public string? PasswordProtected { get; set; }

    [JsonPropertyName("useForAllPaths")]
    public bool UseForAllPaths { get; set; } = true;

    /// <summary>Runtime only. Never serialized.</summary>
    [JsonIgnore]
    public string? PlainPassword { get; set; }

    public bool Matches(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            return false;
        }

        var server = Server.Trim();
        var candidate = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        // Allow "host/path" and bare "host" forms.
        var slash = server.IndexOf('/');
        var hostPart = slash < 0 ? server : server[..slash];
        var pathPart = slash < 0 ? string.Empty : server[slash..];

        if (!hostPart.Equals(candidate, StringComparison.OrdinalIgnoreCase) &&
            !hostPart.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return UseForAllPaths
               || pathPart.Length == 0
               || uri.AbsolutePath.StartsWith(pathPart, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A single entry of the "file types" takeover rule list.</summary>
public sealed class FileTypeRule
{
    [JsonPropertyName("extension")]
    public string Extension { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public FileTypeAction Action { get; set; } = FileTypeAction.TakeOver;

    [JsonPropertyName("category")]
    public DownloadCategory Category { get; set; } = DownloadCategory.Other;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Normalizes to a leading-dot lowercase form such as ".zip".</summary>
    public static string Normalize(string extension)
    {
        var value = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (value.StartsWith("*", StringComparison.Ordinal))
        {
            value = value[1..];
        }
        if (value.Length > 0 && value[0] != '.')
        {
            value = "." + value;
        }
        return value;
    }

    /// <summary>Form used by the options dialog, e.g. "*.zip".</summary>
    [JsonIgnore]
    public string Mask => "*" + Normalize(Extension);
}

/// <summary>An HTTP request/response description produced by probing a URL.</summary>
public sealed class ProbeResult
{
    public bool Success { get; init; }
    public long ContentLength { get; init; } = -1;
    public bool SupportsRanges { get; init; }
    public string? SuggestedFileName { get; init; }
    public string? MimeType { get; init; }
    public string? FinalUrl { get; init; }
    public int StatusCode { get; init; }
    public string? ContentDisposition { get; init; }

    /// <summary>Entity tag of the resource, used to detect that a file changed between resume attempts.</summary>
    public string? ETag { get; init; }

    /// <summary>Last-Modified value, the fallback validator when no ETag is offered.</summary>
    public string? LastModified { get; init; }

    public string? ErrorMessage { get; init; }
    public DownloadErrorKind ErrorKind { get; init; } = DownloadErrorKind.None;

    public static ProbeResult Failed(string message, DownloadErrorKind kind = DownloadErrorKind.Unknown)
        => new() { Success = false, ErrorMessage = message, ErrorKind = kind };
}

/// <summary>
/// Everything needed to enqueue a download. Used by the CLI, the "add URL" dialog,
/// the clipboard monitor and the browser-extension IPC bridge.
/// </summary>
public sealed class AddDownloadRequest
{
    public string Url { get; set; } = string.Empty;
    public string? PageUrl { get; set; }
    public string? Referer { get; set; }
    public string? FileName { get; set; }
    public string? Directory { get; set; }
    public string? Description { get; set; }
    public string? MimeType { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public long TotalBytes { get; set; } = -1;
    public int? Connections { get; set; }
    public int QueueId { get; set; }
    public DownloadCategory? Category { get; set; }
    /// <summary>Start immediately instead of leaving the item queued.</summary>
    public bool StartNow { get; set; } = true;
    /// <summary>Queue the item and let the scheduler pick it up.</summary>
    public bool AddToQueue { get; set; }
}
