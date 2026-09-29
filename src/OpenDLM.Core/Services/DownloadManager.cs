using System.Security.Cryptography;
using OpenDLM.Core.Http;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>Raised when the engine needs the user to supply credentials for a protected server.</summary>
public sealed class CredentialsRequiredEventArgs : EventArgs
{
    public required DownloadItem Item { get; init; }
    public required string Host { get; init; }
    /// <summary>Set to true by the handler once credentials were supplied and the download should continue.</summary>
    public bool CredentialsSupplied { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    /// <summary>When true the supplied credentials are also stored for this server.</summary>
    public bool Remember { get; set; }
}

/// <summary>
/// The download engine's public surface: everything the UI, the CLI and the
/// browser-extension bridge use to control downloads.
///
/// Threading contract: every method is safe to call from any thread. Item property
/// updates may therefore be raised on a background thread, which is fine for the
/// scalar bindings the grid uses; the owning UI is responsible for marshalling the
/// collection-level <see cref="ItemAdded"/> / <see cref="ItemRemoved"/> events.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DownloadStore _store;
    private readonly HttpClientProvider _http;
    private readonly SegmentedDownloader _downloader = new();
    private readonly ThroughputGovernor _governor = new();

    private readonly object _itemsGate = new();
    private readonly List<DownloadItem> _items = new();
    private readonly List<DownloadQueue> _queues;
    private readonly List<SiteLogin> _siteLogins;

    private readonly Dictionary<Guid, RunHandle> _runs = new();
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private readonly Timer _tickTimer;
    private readonly Timer _persistTimer;
    private readonly CancellationTokenSource _shutdown = new();

    private bool _disposed;
    private DateTime _lastPersist = DateTime.UtcNow;

    public DownloadManager(SettingsService settingsService, FileTypeRegistry fileTypes, DownloadStore store)
    {
        _settingsService = settingsService;
        FileTypes = fileTypes;
        _store = store;

        _http = new HttpClientProvider(() => _settingsService.Current);
        _queues = _store.LoadQueues();
        _siteLogins = _store.LoadSiteLogins();

        foreach (var item in _store.LoadDownloads())
        {
            _items.Add(item);
        }

        ApplyGovernorSettings();

        _tickTimer = new Timer(_ => Tick(), null, 500, 500);
        _persistTimer = new Timer(_ => PersistIfDue(), null, 5000, 5000);

        _settingsService.Changed += (_, _) => ApplyGovernorSettings();
    }

    // ------------------------------------------------------------------ public state

    public FileTypeRegistry FileTypes { get; }

    public ThroughputGovernor Governor => _governor;

    public HttpClientProvider HttpClientProvider => _http;

    /// <summary>A stable copy of the download list.</summary>
    public IReadOnlyList<DownloadItem> Snapshot()
    {
        lock (_itemsGate)
        {
            return _items.ToList();
        }
    }

    public IReadOnlyList<DownloadQueue> Queues
    {
        get
        {
            lock (_itemsGate)
            {
                return _queues.OrderBy(q => q.Id).ToList();
            }
        }
    }

    public IReadOnlyList<SiteLogin> SiteLogins
    {
        get
        {
            lock (_itemsGate)
            {
                return _siteLogins.ToList();
            }
        }
    }

    public AppSettings Settings => _settingsService.Current;

    /// <summary>Number of downloads currently occupying a slot.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_runs)
            {
                return _runs.Count;
            }
        }
    }

    /// <summary>Aggregate throughput of all running downloads, in bytes per second.</summary>
    public double TotalSpeed
    {
        get
        {
            lock (_itemsGate)
            {
                return _items.Where(i => i.IsActive).Sum(i => i.Speed);
            }
        }
    }

    public bool HasActiveDownloads
    {
        get
        {
            lock (_runs)
            {
                return _runs.Count > 0;
            }
        }
    }

    // ---------------------------------------------------------------------- events

    public event EventHandler<DownloadItem>? ItemAdded;
    public event EventHandler<DownloadItem>? ItemRemoved;
    public event EventHandler<DownloadItem>? ItemCompleted;
    public event EventHandler<DownloadItem>? ItemFailed;
    public event EventHandler<DownloadItem>? ItemStarted;
    public event EventHandler<DownloadItem>? ItemStatusChanged;
    public event EventHandler<string>? Message;
    public event EventHandler<CredentialsRequiredEventArgs>? CredentialsRequired;
    public event EventHandler? AllCompleted;

    // ------------------------------------------------------------------- mutation

    private sealed class RunHandle
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public required DownloadProgressSink Sink { get; init; }
        /// <summary>Assigned immediately after construction, once the worker task exists.</summary>
        public Task Task { get; set; } = Task.CompletedTask;
        public string? ETag { get; init; }
        public string? LastModified { get; init; }
        public long LastSampleBytes { get; set; }
        public double SmoothedSpeed { get; set; }
        public DateTime LastSampleUtc { get; set; } = DateTime.UtcNow;
        /// <summary>True when the user asked to pause rather than stop.</summary>
        public bool PauseRequested { get; set; }
    }

    /// <summary>Builds a fully described item from an add request without touching the network.</summary>
    public DownloadItem CreateItem(AddDownloadRequest request)
    {
        var settings = _settingsService.Current;
        var url = (request.Url ?? string.Empty).Trim();

        var item = new DownloadItem
        {
            Url = url,
            FinalUrl = url,
            PageUrl = request.PageUrl,
            Referer = request.Referer ?? request.PageUrl,
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? "Added by the user"
                : request.Description,
            Cookies = request.Cookies,
            UserAgent = request.UserAgent,
            Username = request.Username,
            Password = request.Password,
            MimeType = request.MimeType,
            QueueId = request.QueueId,
            TotalBytes = request.TotalBytes > 0 ? request.TotalBytes : -1,
            Connections = request.Connections ?? settings.Connection.MaxConnectionsPerFile
        };

        // File name: explicit > URL segment > placeholder resolved after probing.
        var fileName = !string.IsNullOrWhiteSpace(request.FileName)
            ? FileNameResolver.Sanitize(request.FileName)
            : FileNameResolver.FromUrl(url) ?? string.Empty;
        item.FileName = string.IsNullOrWhiteSpace(fileName) ? string.Empty : fileName;

        var category = request.Category ?? FileTypes.CategoryOf(item.FileName.Length > 0 ? item.FileName : url);
        item.Category = category;

        item.Directory = !string.IsNullOrWhiteSpace(request.Directory)
            ? request.Directory!
            : ResolveDirectory(category, settings);

        item.Status = request.AddToQueue ? DownloadStatus.Queued : DownloadStatus.Queued;
        return item;
    }

    /// <summary>Returns the folder a category is filed into, honouring the "use category folders" setting.</summary>
    public string ResolveDirectory(DownloadCategory category, AppSettings? settings = null)
    {
        settings ??= _settingsService.Current;
        var root = settings.Downloads.DefaultDownloadDirectory;

        if (!settings.Downloads.UseCategoryFolders)
        {
            return root;
        }

        var key = category switch
        {
            DownloadCategory.Video => "Video",
            DownloadCategory.Music => "Music",
            DownloadCategory.Program => "Programs",
            DownloadCategory.Document => "Documents",
            DownloadCategory.Compressed => "Compressed",
            _ => "Other"
        };

        return settings.Downloads.CategoryFolders.TryGetValue(key, out var folder) &&
               !string.IsNullOrWhiteSpace(folder)
            ? folder
            : root;
    }

    /// <summary>Adds an item to the list without starting it.</summary>
    public void Add(DownloadItem item)
    {
        lock (_itemsGate)
        {
            _items.Add(item);
        }

        ItemAdded?.Invoke(this, item);
        PersistNow();
    }

    /// <summary>
    /// The one-call entry point used by the UI, the CLI and the browser bridge.
    /// Adds the item, starts it when requested, and returns the item either way.
    /// </summary>
    public async Task<DownloadItem> AddAndStartAsync(AddDownloadRequest request, CancellationToken cancellationToken = default)
    {
        var item = CreateItem(request);
        Add(item);

        if (request.AddToQueue)
        {
            item.Status = DownloadStatus.Queued;
            return item;
        }

        if (request.StartNow)
        {
            await StartAsync(item, cancellationToken).ConfigureAwait(false);
        }

        return item;
    }

    /// <summary>Immediately probes a URL so the UI can show the resolved name and size.</summary>
    public async Task<ProbeResult> ProbeAsync(string url, DownloadItem? template = null, CancellationToken cancellationToken = default)
    {
        var context = BuildContext();
        var probeTarget = template ?? new DownloadItem { Url = url, FinalUrl = url };
        return await HttpProbe.ProbeAsync(url, context, probeTarget, cancellationToken).ConfigureAwait(false);
    }

    private DownloadContext BuildContext() => new()
    {
        Client = _http.Get(),
        Settings = _settingsService.Current,
        Governor = _governor,
        SiteLogins = SiteLogins,
        LogMessage = message => Log.Info(message)
    };

    // ------------------------------------------------------------------- commands

    public async Task<bool> StartAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_runs)
        {
            if (_runs.ContainsKey(item.Id))
            {
                return false;
            }
        }

        if (item.Status == DownloadStatus.Complete && File.Exists(item.FullPath))
        {
            return false;
        }

        var maxConcurrent = Math.Max(1, _settingsService.Current.Connection.MaxConcurrentDownloads);
        if (ActiveCount >= maxConcurrent)
        {
            item.Status = DownloadStatus.Queued;
            item.ClearError();
            Message?.Invoke(this, $"'{item.FileName}' is waiting for a free download slot.");
            return false;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var sink = new DownloadProgressSink();
        var handle = new RunHandle
        {
            Cancellation = cts,
            Sink = sink
        };

        // Seed the byte counter from the resume state so speed maths starts sane.
        var existing = ResumeStateStore.Load(item);
        if (existing is not null)
        {
            sink.Seed(existing.Segments.Sum(s => s.BytesWritten));
            item.DownloadedBytes = Math.Max(item.DownloadedBytes, sink.Bytes);
        }

        lock (_runs)
        {
            _runs[item.Id] = handle;
        }

        item.ClearError();
        item.Status = DownloadStatus.Connecting;
        item.StartedAt ??= DateTime.Now;
        item.LastTryAt = DateTime.Now;
        ItemStarted?.Invoke(this, item);

        // The handle is published before the worker starts so a Pause/Stop issued
        // from the UI thread is never lost.
        var task = Task.Run(() => ExecuteAsync(item, handle, cts, sink), CancellationToken.None);
        handle.Task = task;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"Unhandled failure while downloading '{item.FileName}'.", ex);
        }

        return true;
    }

    /// <summary>Pauses a running download, keeping the resume state so it can continue later.</summary>
    public void Pause(DownloadItem item)
    {
        RunHandle? handle;
        lock (_runs)
        {
            _runs.TryGetValue(item.Id, out handle);
        }

        if (handle is null)
        {
            if (item.Status is DownloadStatus.Queued or DownloadStatus.Scheduled)
            {
                item.Status = DownloadStatus.Paused;
                ItemStatusChanged?.Invoke(this, item);
            }
            return;
        }

        handle.PauseRequested = true;
        handle.Cancellation.Cancel();
        Message?.Invoke(this, $"Pausing '{item.FileName}'...");
    }

    /// <summary>Stops a running or queued download, keeping it in the list for a later restart.</summary>
    public void Stop(DownloadItem item)
    {
        RunHandle? handle;
        lock (_runs)
        {
            _runs.TryGetValue(item.Id, out handle);
        }

        if (handle is null)
        {
            if (item.Status is DownloadStatus.Queued or DownloadStatus.Scheduled or DownloadStatus.Paused)
            {
                item.Status = DownloadStatus.Stopped;
                item.Speed = 0;
                item.TimeLeft = null;
                ItemStatusChanged?.Invoke(this, item);
            }
            return;
        }

        handle.PauseRequested = false;
        handle.Cancellation.Cancel();
        Message?.Invoke(this, $"Stopping '{item.FileName}'...");
    }

    public void PauseAll()
    {
        foreach (var item in Snapshot().Where(i => i.IsActive))
        {
            Pause(item);
        }
    }

    public void StopAll()
    {
        foreach (var item in Snapshot().Where(i => i.IsActive || i.Status == DownloadStatus.Queued))
        {
            Stop(item);
        }
    }

    public async Task StartAllAsync()
    {
        var candidates = Snapshot()
            .Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Stopped
                or DownloadStatus.Error or DownloadStatus.Queued)
            .Where(i => i.CanStart || i.Status == DownloadStatus.Queued)
            .ToList();

        foreach (var item in candidates)
        {
            // StartAsync already honours the concurrency limit and queues the rest.
            _ = StartAsync(item);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Removes an item, optionally deleting the partial or finished file from disk.</summary>
    public void Remove(DownloadItem item, bool deleteFiles)
    {
        RunHandle? handle;
        lock (_runs)
        {
            _runs.TryGetValue(item.Id, out handle);
        }

        if (handle is not null)
        {
            handle.PauseRequested = false;
            handle.Cancellation.Cancel();
        }

        if (deleteFiles)
        {
            ResumeStateStore.Delete(item);
            TryDeleteFile(item.PartialPath);
            TryDeleteFile(item.FullPath);
        }

        lock (_itemsGate)
        {
            _items.Remove(item);
        }

        lock (_runs)
        {
            _runs.Remove(item.Id);
        }

        ItemRemoved?.Invoke(this, item);
        PersistNow();
    }

    public void RemoveCompleted(bool deleteFiles)
    {
        foreach (var item in Snapshot().Where(i => i.Status == DownloadStatus.Complete).ToList())
        {
            Remove(item, deleteFiles);
        }
    }

    /// <summary>Restarts a finished or failed download from byte zero.</summary>
    public async Task RestartAsync(DownloadItem item, bool deletePartial = true)
    {
        if (deletePartial)
        {
            ResumeStateStore.Delete(item);
            ResumeStateStore.DeletePartialFile(item);
            item.Segments.Clear();
            item.DownloadedBytes = 0;
            item.TotalBytes = -1;
            item.RetryCount = 0;
        }

        item.ClearError();
        await StartAsync(item).ConfigureAwait(false);
    }

    /// <summary>Finds an existing entry for the same URL, used for the duplicate warning.</summary>
    public DownloadItem? FindByUrl(string url)
    {
        lock (_itemsGate)
        {
            return _items.FirstOrDefault(i =>
                string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool HasCompleteItemFor(string fullPath)
    {
        lock (_itemsGate)
        {
            return _items.Any(i => i.Status == DownloadStatus.Complete &&
                                   string.Equals(i.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---------------------------------------------------------------- the worker

    private async Task ExecuteAsync(
        DownloadItem item, RunHandle handle, CancellationTokenSource cts, DownloadProgressSink sink)
    {
        var attempt = 0;

        try
        {
            while (true)
            {
                var outcome = await RunOnceAsync(item, cts.Token, sink).ConfigureAwait(false);

                if (outcome.Success)
                {
                    Complete(item, outcome.TotalBytes);
                    return;
                }

                if (outcome.Cancelled)
                {
                    item.Speed = 0;
                    item.TimeLeft = null;
                    // Pause keeps the item resumable; stop leaves it plainly halted.
                    item.Status = handle.PauseRequested ? DownloadStatus.Paused : DownloadStatus.Stopped;
                    ItemStatusChanged?.Invoke(this, item);
                    return;
                }

                if (outcome.AuthenticationRequired)
                {
                    var supplied = await RequestCredentialsAsync(item).ConfigureAwait(false);
                    if (supplied)
                    {
                        attempt = 0;
                        continue;
                    }
                    Fail(item, outcome.ErrorKind, outcome.ErrorMessage ?? "Authentication is required.");
                    return;
                }

                var settings = _settingsService.Current;
                var canRetry = settings.Downloads.AutoRetryOnFailure &&
                               ErrorMapper.IsRetryable(outcome.ErrorKind) &&
                               attempt < Math.Max(0, settings.Connection.RetryCount) &&
                               !cts.IsCancellationRequested;

                if (outcome.RangeFallbackRequired && settings.Connection.SingleConnectionFallback)
                {
                    Log.Info($"'{item.FileName}': falling back to a single connection.");
                    Message?.Invoke(this, $"'{item.FileName}': the server does not support segments; using one connection.");
                    continue;
                }

                if (!canRetry)
                {
                    Fail(item, outcome.ErrorKind, outcome.ErrorMessage ?? "The download failed.");
                    return;
                }

                attempt++;
                item.RetryCount = attempt;
                var delay = Math.Max(1, settings.Connection.RetryDelaySeconds);
                Message?.Invoke(this, $"Retrying '{item.FileName}' in {delay}s (attempt {attempt}).");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    item.Status = DownloadStatus.Stopped;
                    return;
                }
            }
        }
        finally
        {
            lock (_runs)
            {
                _runs.Remove(item.Id);
            }
            cts.Dispose();
            item.Speed = 0;
            item.TimeLeft = null;
            item.ActiveConnections = 0;
            PersistNow();

            // A slot just freed up: pull the next queued item forward.
            _ = PumpQueuedAsync();

            if (!HasActiveDownloads && !Snapshot().Any(i => i.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Connecting))
            {
                AllCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task<DownloadOutcome> RunOnceAsync(DownloadItem item, CancellationToken cancellationToken, DownloadProgressSink sink)
    {
        var context = BuildContext();
        var settings = context.Settings;

        item.Status = DownloadStatus.Connecting;

        // ---- probe -----------------------------------------------------------
        var probe = await HttpProbe.ProbeAsync(item.Url, context, item, cancellationToken).ConfigureAwait(false);
        if (!probe.Success)
        {
            if (probe.ErrorKind == DownloadErrorKind.AuthenticationRequired)
            {
                return DownloadOutcome.NeedsCredentials(probe.ErrorMessage ?? "Authentication is required.");
            }
            return DownloadOutcome.Failed(probe.ErrorKind, probe.ErrorMessage ?? "The server did not answer correctly.");
        }

        item.FinalUrl = probe.FinalUrl;
        item.MimeType ??= probe.MimeType;

        if (string.IsNullOrWhiteSpace(item.FileName))
        {
            item.FileName = FileNameResolver.Sanitize(probe.SuggestedFileName);
        }

        if (string.IsNullOrWhiteSpace(item.Directory))
        {
            item.Directory = ResolveDirectory(item.Category, settings);
        }

        Directory.CreateDirectory(item.Directory);

        // Resolve the on-disk name once the real name is known: never silently
        // clobber an unrelated existing file.
        await EnsureDestinationAvailableAsync(item, probe.ContentLength).ConfigureAwait(false);

        if (probe.ContentLength > 0 && item.TotalBytes != probe.ContentLength)
        {
            item.TotalBytes = probe.ContentLength;
        }
        else if (probe.ContentLength <= 0 && item.TotalBytes <= 0)
        {
            item.TotalBytes = -1;
        }

        item.SupportsRanges = probe.SupportsRanges;
        item.IsResumable = probe.SupportsRanges;

        // ---- plan ------------------------------------------------------------
        List<Segment>? resumed = probe.SupportsRanges ? ResumeStateStore.TryResume(item, probe) : null;

        if (resumed is null)
        {
            // Starting fresh: throw away any stale partial data so an unknown-length
            // download can never append to bytes from a previous attempt.
            ResumeStateStore.DeletePartialFile(item);
            item.Segments.Clear();
        }
        else
        {
            item.Segments = resumed;
        }

        var requestedConnections = Math.Clamp(
            item.Connections > 0 ? item.Connections : settings.Connection.MaxConnectionsPerFile,
            1, 32);

        if (!probe.SupportsRanges)
        {
            requestedConnections = 1;
        }

        if (item.Segments.Count == 0)
        {
            item.Segments = SegmentPlanner.Create(item.TotalBytes, requestedConnections);
        }

        var plan = new DownloadPlan
        {
            Segments = item.Segments,
            TotalBytes = item.TotalBytes,
            SupportsRanges = probe.SupportsRanges,
            MaxParallelSegments = Math.Max(1, item.Segments.Count),
            PersistResume = () => ResumeStateStore.Save(item, new ResumeState
            {
                ETag = probe.ETag,
                LastModified = probe.LastModified,
                Connections = item.Segments.Count
            })
        };

        item.Connections = Math.Max(1, item.Segments.Count);
        item.Status = item.DownloadedBytes > 0 ? DownloadStatus.Downloading : DownloadStatus.Connecting;

        // Make sure the sink reflects bytes already on disk, so the progress bar is
        // correct the moment a resumed download starts.
        var already = item.Segments.Sum(s => s.BytesWritten);
        sink.Seed(already);
        item.DownloadedBytes = already;

        // ---- run -------------------------------------------------------------
        item.Status = DownloadStatus.Downloading;
        var outcome = await _downloader.RunAsync(item, context, plan, sink, cancellationToken).ConfigureAwait(false);

        if (outcome.Success)
        {
            item.DownloadedBytes = outcome.TotalBytes > 0 ? outcome.TotalBytes : sink.Bytes;
        }

        return outcome;
    }

    /// <summary>Chooses a non-conflicting destination file name according to the user's preference.</summary>
    private async Task EnsureDestinationAvailableAsync(DownloadItem item, long remoteSize)
    {
        var action = _settingsService.Current.Downloads.ExistingFileAction;

        if (!File.Exists(item.FullPath))
        {
            return;
        }

        switch (action)
        {
            case ExistingFileAction.Overwrite:
                TryDeleteFile(item.FullPath);
                break;

            case ExistingFileAction.Rename:
                item.FileName = FileNameResolver.MakeUnique(item.Directory, item.FileName);
                break;

            case ExistingFileAction.Resume:
                // A complete file of the right size already satisfies the request.
                if (remoteSize > 0 && new FileInfo(item.FullPath).Length == remoteSize)
                {
                    Message?.Invoke(this, $"'{item.FileName}' already exists with the correct size; skipping.");
                }
                else
                {
                    item.FileName = FileNameResolver.MakeUnique(item.Directory, item.FileName);
                }
                break;

            case ExistingFileAction.Ask:
            default:
                // The UI asks before the download starts; if it could not (CLI, browser),
                // renaming is the non-destructive default.
                item.FileName = FileNameResolver.MakeUnique(item.Directory, item.FileName);
                break;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<bool> RequestCredentialsAsync(DownloadItem item)
    {
        var handler = CredentialsRequired;
        if (handler is null)
        {
            return false;
        }

        var host = Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host : item.Url;
        var args = new CredentialsRequiredEventArgs { Item = item, Host = host };

        // Hop to the UI thread if the subscriber needs it; the handler is synchronous
        // by contract so we can simply invoke and inspect the flag.
        handler.Invoke(this, args);
        await Task.CompletedTask.ConfigureAwait(false);

        if (!args.CredentialsSupplied)
        {
            return false;
        }

        item.Username = args.Username;
        item.Password = args.Password;

        if (args.Remember && !string.IsNullOrWhiteSpace(args.Username))
        {
            var login = _siteLogins.FirstOrDefault(l =>
                string.Equals(l.Server, host, StringComparison.OrdinalIgnoreCase));
            if (login is null)
            {
                login = new SiteLogin { Server = host, Username = args.Username! };
                lock (_itemsGate)
                {
                    _siteLogins.Add(login);
                }
            }
            login.Username = args.Username!;
            login.PlainPassword = args.Password;
            login.PasswordProtected = CredentialProtector.Protect(args.Password);
            SaveSiteLogins();
        }

        return true;
    }

    private void Complete(DownloadItem item, long totalBytes)
    {
        item.Status = DownloadStatus.Finalizing;

        try
        {
            if (totalBytes > 0)
            {
                item.TotalBytes = totalBytes;
            }

            // Move the preallocated partial file into place. Same volume, so this is
            // an atomic metadata operation rather than a copy.
            if (File.Exists(item.FullPath))
            {
                TryDeleteFile(item.FullPath);
            }

            if (File.Exists(item.PartialPath))
            {
                File.Move(item.PartialPath, item.FullPath, overwrite: true);
            }

            ResumeStateStore.Delete(item);

            if (item.TotalBytes <= 0 && File.Exists(item.FullPath))
            {
                item.TotalBytes = new FileInfo(item.FullPath).Length;
            }

            item.DownloadedBytes = Math.Max(item.DownloadedBytes, item.TotalBytes);
            item.Status = DownloadStatus.Complete;
            item.CompletedAt = DateTime.Now;
            item.Speed = 0;
            item.TimeLeft = null;
            item.ErrorKind = DownloadErrorKind.None;
            item.ErrorMessage = null;

            if (_settingsService.Current.Downloads.ComputeChecksums)
            {
                ComputeChecksums(item);
            }

            Log.Info($"Completed '{item.FileName}' ({Fmt.Bytes(item.TotalBytes)}) to {item.FullPath}.");
            Message?.Invoke(this, $"Download complete: {item.FileName}");
            ItemCompleted?.Invoke(this, item);
        }
        catch (Exception ex)
        {
            var (kind, message) = ErrorMapper.Map(ex);
            Fail(item, kind, "The file could not be moved into place: " + message);
        }
        finally
        {
            ItemStatusChanged?.Invoke(this, item);
            PersistNow();
        }
    }

    private void ComputeChecksums(DownloadItem item)
    {
        try
        {
            if (!File.Exists(item.FullPath))
            {
                return;
            }

            using var stream = File.OpenRead(item.FullPath);
            using var md5 = MD5.Create();
            item.ChecksumMd5 = Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();

            stream.Position = 0;
            using var sha1 = SHA1.Create();
            item.ChecksumSha1 = Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not compute checksums for '" + item.FileName + "': " + ex.Message);
        }
    }

    private void Fail(DownloadItem item, DownloadErrorKind kind, string message)
    {
        item.Status = DownloadStatus.Error;
        item.ErrorKind = kind;
        item.ErrorMessage = message;
        item.Speed = 0;
        item.TimeLeft = null;

        Log.Warn($"Download failed: '{item.FileName}' - {kind}: {message}");
        Message?.Invoke(this, $"'{item.FileName}' failed: {message}");
        ItemFailed?.Invoke(this, item);
        ItemStatusChanged?.Invoke(this, item);
    }

    // -------------------------------------------------------------- queue pumping

    /// <summary>Starts queued items while download slots and queue windows allow it.</summary>
    public async Task PumpQueuedAsync()
    {
        if (!await _pumpGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var settings = _settingsService.Current;
            var maxConcurrent = Math.Max(1, settings.Connection.MaxConcurrentDownloads);

            while (ActiveCount < maxConcurrent)
            {
                var next = Snapshot()
                    .Where(i => i.Status is DownloadStatus.Queued or DownloadStatus.Scheduled)
                    .Where(i => IsQueueActive(i.QueueId, DateTime.Now))
                    .OrderBy(i => i.CreatedAt)
                    .FirstOrDefault();

                if (next is null)
                {
                    return;
                }

                next.Status = DownloadStatus.Connecting;
                // StartAsync re-checks the slot count; it may immediately re-queue.
                var started = await StartAsync(next).ConfigureAwait(false);
                if (!started && next.Status == DownloadStatus.Queued)
                {
                    // The slot count changed under us; stop pumping to avoid a hot loop.
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Queue pump failed.", ex);
        }
        finally
        {
            _pumpGate.Release();
        }
    }

    /// <summary>True when the queue may run right now, taking overnight windows into account.</summary>
    public bool IsQueueActive(int queueId, DateTime now)
    {
        DownloadQueue? queue;
        lock (_itemsGate)
        {
            queue = _queues.FirstOrDefault(q => q.Id == queueId);
        }

        if (queue is null || !queue.Enabled)
        {
            return queueId == 0;
        }

        if (!queue.Scheduled)
        {
            return true;
        }

        if (!queue.RunsOn(now.DayOfWeek))
        {
            // A window that runs past midnight belongs to the previous day.
            var yesterday = now.AddDays(-1).DayOfWeek;
            if (!(queue.StartTime > queue.StopTime && queue.RunsOn(yesterday) && now.TimeOfDay < queue.StopTime))
            {
                return false;
            }
        }

        var time = now.TimeOfDay;
        if (queue.StartTime <= queue.StopTime)
        {
            return time >= queue.StartTime && time < queue.StopTime;
        }

        // Overnight window, e.g. 23:00 - 07:00.
        return time >= queue.StartTime || time < queue.StopTime;
    }

    public void SaveQueues(IEnumerable<DownloadQueue> queues)
    {
        lock (_itemsGate)
        {
            _queues.Clear();
            _queues.AddRange(queues.OrderBy(q => q.Id));
        }
        _store.SaveQueues(_queues);
    }

    public void SaveSiteLogins()
    {
        List<SiteLogin> snapshot;
        lock (_itemsGate)
        {
            snapshot = _siteLogins.ToList();
        }
        _store.SaveSiteLogins(snapshot);
    }

    /// <summary>Adds or updates a stored site login.</summary>
    public void UpsertSiteLogin(SiteLogin login)
    {
        lock (_itemsGate)
        {
            var existing = _siteLogins.FirstOrDefault(l => l.Id == login.Id);
            if (existing is null)
            {
                _siteLogins.Add(login);
            }
            else
            {
                existing.Server = login.Server;
                existing.Username = login.Username;
                existing.Realm = login.Realm;
                existing.UseForAllPaths = login.UseForAllPaths;
                existing.PlainPassword = login.PlainPassword;
                existing.PasswordProtected = CredentialProtector.Protect(login.PlainPassword);
            }
        }
        SaveSiteLogins();
    }

    public void RemoveSiteLogin(SiteLogin login)
    {
        lock (_itemsGate)
        {
            _siteLogins.RemoveAll(l => l.Id == login.Id);
        }
        SaveSiteLogins();
    }

    // -------------------------------------------------------------------- tickers

    private void ApplyGovernorSettings()
    {
        var connection = _settingsService.Current.Connection;
        _governor.ApplyLimit(connection.SpeedLimitEnabled, connection.SpeedLimitKbPerSecond);
    }

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            List<(DownloadItem Item, RunHandle Handle)> running;
            lock (_runs)
            {
                running = _runs
                    .Select(pair => (Item: FindItem(pair.Key), Handle: pair.Value))
                    .Where(pair => pair.Item is not null)
                    .Select(pair => (pair.Item!, pair.Handle))
                    .ToList();
            }

            var now = DateTime.UtcNow;
            foreach (var (item, handle) in running)
            {
                var bytes = handle.Sink.Bytes;
                item.DownloadedBytes = bytes;
                // The live connection count is a separate property; the planned
                // segment count belongs to RunOnceAsync.
                item.ActiveConnections = handle.Sink.ActiveConnections;

                var elapsed = (now - handle.LastSampleUtc).TotalSeconds;
                if (elapsed <= 0.05)
                {
                    continue;
                }

                var instantaneous = (bytes - handle.LastSampleBytes) / elapsed;
                if (instantaneous < 0)
                {
                    instantaneous = 0;
                }

                // Exponential moving average: the instantaneous value is far too
                // jittery to display directly.
                handle.SmoothedSpeed = handle.SmoothedSpeed <= 0.01
                    ? instantaneous
                    : (handle.SmoothedSpeed * 0.65) + (instantaneous * 0.35);

                handle.LastSampleBytes = bytes;
                handle.LastSampleUtc = now;

                item.Speed = handle.SmoothedSpeed;
                item.ElapsedSeconds += elapsed;

                if (item.ElapsedSeconds > 0.5)
                {
                    item.AverageSpeed = item.DownloadedBytes / item.ElapsedSeconds;
                }

                if (item.TotalBytes > 0 && handle.SmoothedSpeed > 1)
                {
                    var remaining = Math.Max(0, item.TotalBytes - bytes);
                    item.TimeLeft = TimeSpan.FromSeconds(remaining / handle.SmoothedSpeed);
                }
                else
                {
                    item.TimeLeft = null;
                }
            }

            // Keep queued work moving (scheduled windows, freed slots).
            if (Snapshot().Any(i => i.Status is DownloadStatus.Queued or DownloadStatus.Scheduled))
            {
                _ = PumpQueuedAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Progress tick failed.", ex);
        }
    }

    private DownloadItem? FindItem(Guid id)
    {
        lock (_itemsGate)
        {
            return _items.FirstOrDefault(i => i.Id == id);
        }
    }

    private void PersistIfDue()
    {
        if (_disposed)
        {
            return;
        }

        if ((DateTime.UtcNow - _lastPersist).TotalSeconds < 5)
        {
            return;
        }

        PersistNow();
    }

    /// <summary>Writes the download list to disk. Cheap enough to call on state changes.</summary>
    public void PersistNow()
    {
        try
        {
            _lastPersist = DateTime.UtcNow;

            // Do not persist finished items with a stale speed value.
            foreach (var item in Snapshot())
            {
                if (!item.IsActive)
                {
                    item.Speed = 0;
                    item.TimeLeft = null;
                }
            }

            _store.SaveDownloads(Snapshot());
        }
        catch (Exception ex)
        {
            Log.Error("Failed to persist the download list.", ex);
        }
    }

    private static void TryDeleteFile(string path)
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _shutdown.Cancel();
            _tickTimer.Dispose();
            _persistTimer.Dispose();
            PersistNow();
            _http.Dispose();
            _pumpGate.Dispose();
            _shutdown.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Error during shutdown: " + ex.Message);
        }
    }
}
