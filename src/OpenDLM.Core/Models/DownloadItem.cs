using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Models;

/// <summary>
/// A single download. This is the engine's unit of work and the object that the
/// main window's list is bound to, so it implements <see cref="INotifyPropertyChanged"/>
/// and exposes display-ready computed properties.
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private DownloadStatus _status = DownloadStatus.Queued;
    private long _downloadedBytes;
    private long _totalBytes;
    private double _speed;
    private TimeSpan? _timeLeft;
    private int _connections = 1;
    private string _fileName = string.Empty;
    private string _directory = AppPaths.DefaultDownloadDirectory;
    private string? _errorMessage;
    private DownloadErrorKind _errorKind = DownloadErrorKind.None;
    private DownloadCategory _category = DownloadCategory.Other;
    private string? _mimeType;
    private string? _checksumMd5;
    private string? _checksumSha1;

    // ---------------------------------------------------------------- identity

    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>URL after redirects; equals <see cref="Url"/> until the server responds.</summary>
    [JsonPropertyName("finalUrl")]
    public string? FinalUrl { get; set; }

    /// <summary>Page the download was started from. Sent as Referer and used for site logins.</summary>
    [JsonPropertyName("pageUrl")]
    public string? PageUrl { get; set; }

    // ------------------------------------------------------------- destination

    [JsonPropertyName("fileName")]
    public string FileName
    {
        get => _fileName;
        set
        {
            if (Set(ref _fileName, value))
            {
                Raise(nameof(FullPath));
            }
        }
    }

    [JsonPropertyName("directory")]
    public string Directory
    {
        get => _directory;
        set
        {
            if (Set(ref _directory, value))
            {
                Raise(nameof(FullPath));
            }
        }
    }

    /// <summary>Final resting place of the finished download.</summary>
    [JsonIgnore]
    public string FullPath => Path.Combine(Directory, FileName);

    /// <summary>File the bytes are actually streamed into while downloading (same volume, so the final move is instant).</summary>
    [JsonIgnore]
    public string PartialPath => FullPath + ".opendlmpart";

    /// <summary>Resume sidecar holding segment positions and request metadata.</summary>
    [JsonIgnore]
    public string ResumePath => FullPath + ".opendlm";

    // ------------------------------------------------------------- engine state

    [JsonPropertyName("status")]
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(StatusText));
                Raise(nameof(IsActive));
                Raise(nameof(IsFinished));
                Raise(nameof(CanStart));
                Raise(nameof(CanPause));
                Raise(nameof(CanStop));
            }
        }
    }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (Set(ref _totalBytes, value))
            {
                Raise(nameof(SizeText));
                Raise(nameof(Progress));
                Raise(nameof(DownloadedText));
            }
        }
    }

    [JsonPropertyName("downloadedBytes")]
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (Set(ref _downloadedBytes, value))
            {
                Raise(nameof(Progress));
                Raise(nameof(DownloadedText));
                Raise(nameof(DownloadedWithTotalText));
            }
        }
    }

    [JsonPropertyName("speed")]
    public double Speed
    {
        get => _speed;
        set
        {
            if (Set(ref _speed, value))
            {
                Raise(nameof(SpeedText));
            }
        }
    }

    [JsonPropertyName("timeLeft")]
    public TimeSpan? TimeLeft
    {
        get => _timeLeft;
        set
        {
            if (Set(ref _timeLeft, value))
            {
                Raise(nameof(TimeLeftText));
            }
        }
    }

    [JsonPropertyName("connections")]
    public int Connections
    {
        get => _connections;
        set => Set(ref _connections, Math.Clamp(value, 1, 32));
    }

    [JsonPropertyName("supportsRanges")]
    public bool SupportsRanges { get; set; }

    [JsonPropertyName("isResumable")]
    public bool IsResumable { get; set; } = true;

    [JsonPropertyName("mimeType")]
    public string? MimeType
    {
        get => _mimeType;
        set
        {
            if (Set(ref _mimeType, value))
            {
                Raise(nameof(TypeText));
            }
        }
    }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("referer")]
    public string? Referer { get; set; }

    [JsonPropertyName("cookies")]
    public string? Cookies { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Encrypted at rest by the settings layer; never written to the log.</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("category")]
    public DownloadCategory Category
    {
        get => _category;
        set
        {
            if (Set(ref _category, value))
            {
                Raise(nameof(CategoryText));
            }
        }
    }

    /// <summary>0 means "not in a named queue" (the default queue).</summary>
    [JsonPropertyName("queueId")]
    public int QueueId { get; set; }

    [JsonPropertyName("errorKind")]
    public DownloadErrorKind ErrorKind
    {
        get => _errorKind;
        set => Set(ref _errorKind, value);
    }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (Set(ref _errorMessage, value))
            {
                Raise(nameof(StatusText));
            }
        }
    }

    [JsonPropertyName("resumeCount")]
    public int ResumeCount { get; set; }

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }

    [JsonPropertyName("segments")]
    public List<Segment> Segments { get; set; } = new();

    // ------------------------------------------------------------------- times

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("lastTryAt")]
    public DateTime? LastTryAt { get; set; }

    /// <summary>Wall-clock time accumulated across all start/pause cycles.</summary>
    [JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds { get; set; }

    /// <summary>Average throughput over the life of the download, in bytes/sec.</summary>
    [JsonPropertyName("averageSpeed")]
    public double AverageSpeed { get; set; }

    [JsonPropertyName("checksumMd5")]
    public string? ChecksumMd5
    {
        get => _checksumMd5;
        set => Set(ref _checksumMd5, value);
    }

    [JsonPropertyName("checksumSha1")]
    public string? ChecksumSha1
    {
        get => _checksumSha1;
        set => Set(ref _checksumSha1, value);
    }

    /// <summary>True once the user has been shown the "download complete" dialog, so it is shown only once.</summary>
    [JsonIgnore]
    public bool CompletionNotified { get; set; }

    // -------------------------------------------------------------- computed UI

    [JsonIgnore]
    public double Progress => Fmt.Percent(_downloadedBytes, _totalBytes);

    [JsonIgnore]
    public bool IsActive => _status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Finalizing;

    [JsonIgnore]
    public bool IsFinished => _status is DownloadStatus.Complete;

    [JsonIgnore]
    public bool CanStart => _status is DownloadStatus.Paused or DownloadStatus.Stopped
        or DownloadStatus.Error or DownloadStatus.Queued or DownloadStatus.Scheduled
        or DownloadStatus.Connecting && _downloadedBytes == 0;

    [JsonIgnore]
    public bool CanPause => _status is DownloadStatus.Downloading or DownloadStatus.Connecting;

    [JsonIgnore]
    public bool CanStop => IsActive || _status == DownloadStatus.Queued;

    [JsonIgnore]
    public string SizeText => _totalBytes > 0 ? Fmt.Bytes(_totalBytes) : "Unknown";

    [JsonIgnore]
    public string DownloadedText => Fmt.Bytes(_downloadedBytes);

    [JsonIgnore]
    public string DownloadedWithTotalText => _totalBytes > 0
        ? $"{Fmt.Bytes(_downloadedBytes)} / {Fmt.Bytes(_totalBytes)}"
        : Fmt.Bytes(_downloadedBytes);

    [JsonIgnore]
    public string SpeedText => IsActive ? Fmt.Speed(_speed) : string.Empty;

    [JsonIgnore]
    public string TimeLeftText => IsActive && _timeLeft.HasValue ? Fmt.Duration(_timeLeft) : string.Empty;

    [JsonIgnore]
    public string StatusText => _status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Connecting => "Connecting...",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Stopped => "Stopped",
        DownloadStatus.Complete => "Complete",
        DownloadStatus.Scheduled => "Scheduled",
        DownloadStatus.Finalizing => "Finalizing...",
        DownloadStatus.Error => string.IsNullOrWhiteSpace(_errorMessage) ? "Error" : "Error: " + Fmt.Ellipsis(_errorMessage, 60),
        _ => "Unknown"
    };

    [JsonIgnore]
    public string CategoryText => _category switch
    {
        DownloadCategory.Video => "Video",
        DownloadCategory.Music => "Music",
        DownloadCategory.Program => "Programs",
        DownloadCategory.Document => "Documents",
        DownloadCategory.Compressed => "Compressed",
        _ => "Other"
    };

    [JsonIgnore]
    public string TypeText
    {
        get
        {
            var ext = Path.GetExtension(_fileName);
            if (!string.IsNullOrEmpty(ext))
            {
                return ext.TrimStart('.').ToUpperInvariant() + " file";
            }
            return string.IsNullOrWhiteSpace(_mimeType) ? "File" : _mimeType!;
        }
    }

    [JsonIgnore]
    public string LastTryText => LastTryAt?.ToString("dd.MM.yyyy HH:mm") ?? string.Empty;

    /// <summary>One-line summary used by the IPC/subscription channel and the status column of the popup.</summary>
    public string ToSummaryString()
        => $"{FileName} - {StatusText} - {Progress:0.0}% - {SpeedText}";

    // ------------------------------------------------------------ notifications

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Refreshes every computed property. Used after a bulk state change from the engine.</summary>
    public void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Status), nameof(StatusText), nameof(Progress), nameof(SizeText),
                     nameof(DownloadedText), nameof(DownloadedWithTotalText), nameof(SpeedText),
                     nameof(TimeLeftText), nameof(CategoryText), nameof(TypeText), nameof(FullPath),
                     nameof(IsActive), nameof(IsFinished), nameof(CanStart), nameof(CanPause), nameof(CanStop)
                 })
        {
            Raise(name);
        }
    }

    /// <summary>Clears error state so a retry starts from a clean slate.</summary>
    public void ClearError()
    {
        ErrorKind = DownloadErrorKind.None;
        ErrorMessage = null;
    }
}
