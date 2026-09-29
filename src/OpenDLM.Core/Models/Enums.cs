namespace OpenDLM.Core.Models;

/// <summary>Lifecycle state of a single download.</summary>
public enum DownloadStatus
{
    /// <summary>Waiting for a free download slot.</summary>
    Queued = 0,

    /// <summary>Resolving the URL, following redirects and probing the server.</summary>
    Connecting = 1,

    /// <summary>Data is flowing.</summary>
    Downloading = 2,

    /// <summary>Suspended by the user; resume state is kept.</summary>
    Paused = 3,

    /// <summary>Stopped; resume state is kept and the item stays in the list.</summary>
    Stopped = 4,

    /// <summary>Finished successfully.</summary>
    Complete = 5,

    /// <summary>Finished with an error; may be retried.</summary>
    Error = 6,

    /// <summary>Scheduled to start later.</summary>
    Scheduled = 7,

    /// <summary>Post-processing: merging parts, verifying size, running actions.</summary>
    Finalizing = 8
}

/// <summary>Bucket a download is filed under in the main window's category tree.</summary>
public enum DownloadCategory
{
    Other = 0,
    Video = 1,
    Music = 2,
    Program = 3,
    Document = 4,
    Compressed = 5
}

/// <summary>How the engine should treat a given file extension when a browser reports a download.</summary>
public enum FileTypeAction
{
    /// <summary>Automatically take the download over from the browser.</summary>
    TakeOver = 0,

    /// <summary>Never take over; let the browser handle it.</summary>
    DoNotTakeOver = 1,

    /// <summary>Ask the user each time.</summary>
    Ask = 2
}

/// <summary>Classification of a download failure, which drives the message shown to the user.</summary>
public enum DownloadErrorKind
{
    None = 0,
    Unknown = 1,
    Dns,
    Timeout,
    ConnectionReset,
    HttpError,
    AuthenticationRequired,
    Forbidden,
    NotFound,
    DiskFull,
    DiskError,
    FileInUse,
    RangeNotSupported,
    Cancelled,
    TooManyRedirects,
    InvalidUrl,
    SizeMismatch
}

/// <summary>Which SOCKS dialect a manual proxy speaks.</summary>
public enum SocksType
{
    None = 0,
    Socks4 = 1,
    Socks5 = 2
}

/// <summary>How much the toolbar shows.</summary>
public enum ToolbarStyle
{
    /// <summary>Small icons with their labels.</summary>
    IconsAndText = 0,

    /// <summary>Icons only, for a compact window.</summary>
    IconsOnly = 1,

    /// <summary>Large icons with the labels underneath.</summary>
    LargeIcons = 2
}
