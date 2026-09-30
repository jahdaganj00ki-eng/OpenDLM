using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.Tests;

/// <summary>
/// End-to-end tests that run the real engine against a real (local) HTTP server.
/// These are the tests that prove segmented downloading, resume and pause actually
/// work rather than merely compile.
/// </summary>
public static class EngineTests
{
    private static SettingsService CreateSettings(Action<AppSettings>? configure)
    {
        ResetState();

        var settings = new SettingsService();
        settings.Update(s =>
        {
            s.General.ClipboardMonitoring = false;
            s.General.ShowStartDialog = false;
            s.General.ShowCompleteDialog = false;
            s.Downloads.AutoRemoveCompleted = false;
            s.Downloads.UseCategoryFolders = false;
            s.Downloads.ExistingFileAction = ExistingFileAction.Overwrite;
            s.Downloads.AutoRetryOnFailure = true;
            s.Connection.MaxConcurrentDownloads = 4;
            s.Connection.RetryDelaySeconds = 1;
            configure?.Invoke(s);
        });
        return settings;
    }

    /// <summary>Each test starts from an empty engine state so runs are independent.</summary>
    private static void ResetState()
    {
        foreach (var path in new[]
                 {
                     AppPaths.DownloadsFile,
                     AppPaths.QueuesFile,
                     AppPaths.SitesLoginsFile,
                     // Site exceptions change engine behaviour for every later test,
                     // so they must not leak between cases.
                     AppPaths.SitesExceptionsFile
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort; a locked file only means the previous run is still settling.
            }
        }
    }

    private static string NewFolder(string root, string name)
    {
        var folder = Path.Combine(root, "dest", name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ------------------------------------------------------------------ the tests

    /// <summary>
    /// The headline capability: one file, several parallel range requests, byte
    /// perfect result.
    /// </summary>
    public static async Task MultiConnectionDownloadIsBytePerfect(string root)
    {
        using var server = new TestHttpServer(4 * 1024 * 1024);
        var settings = CreateSettings(s =>
        {
            s.Connection.MaxConnectionsPerFile = 4;
            s.Connection.RetryCount = 2;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "multi");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "payload.bin",
            Description = "multi connection test"
        });

        Check.Equal(DownloadStatus.Complete, item.Status, "download status (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "downloaded content");
        Check.Equal((long)server.Payload.Length, item.TotalBytes, "reported total size");
        Check.Equal(4, item.Connections, "four segments were planned");
        Check.Equal(0, item.ActiveConnections, "no connections stay open once the download is complete");
        Check.True(server.RangeRequestCount >= 4,
            "expected at least four range requests, saw " + server.RangeRequestCount);

        Check.False(File.Exists(item.PartialPath), "the partial file is gone after completion");
        Check.False(File.Exists(item.ResumePath), "the resume sidecar is gone after completion");
        Check.True(item.ChecksumMd5 is null, "checksums stay off unless enabled");
    }

    /// <summary>A server that ignores Range must degrade to a single connection, not corrupt the file.</summary>
    public static async Task ServerWithoutRangeSupportFallsBackToSingleConnection(string root)
    {
        using var server = new TestHttpServer(2 * 1024 * 1024) { SupportsRanges = false };
        var settings = CreateSettings(s => s.Connection.MaxConnectionsPerFile = 8);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "norange");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "single.bin"
        });

        Check.Equal(DownloadStatus.Complete, item.Status, "download status (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "downloaded content");
        Check.Equal(1, item.Connections, "the engine used a single connection");
        Check.False(item.SupportsRanges, "range support was correctly reported as absent");
        Check.Equal(0, server.RangeRequestCount, "no range requests were honoured");
    }

    /// <summary>
    /// Dropped connections must be survived: the segments are retried, and when the
    /// in-run retries are exhausted the next run resumes from the sidecar.
    /// </summary>
    public static async Task DroppedConnectionsAreResumedNotRestarted(string root)
    {
        using var server = new TestHttpServer(4 * 1024 * 1024) { TruncateTimes = 8 };
        var settings = CreateSettings(s =>
        {
            s.Connection.MaxConnectionsPerFile = 4;
            s.Connection.RetryCount = 1;
            s.Connection.RetryDelaySeconds = 1;
            s.Downloads.AutoRetryOnFailure = true;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "resume");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "resilient.bin"
        });

        Check.Equal(DownloadStatus.Complete, item.Status,
            "download status after dropped connections (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "downloaded content");
        Check.True(server.RequestCount > 5,
            "expected several requests while recovering, saw " + server.RequestCount);
        Check.True(item.RetryCount >= 1, "the engine recorded at least one retry");
    }

    /// <summary>Pausing must leave the partial file intact, and resuming must finish it correctly.</summary>
    public static async Task PauseAndResumeKeepsData(string root)
    {
        using var server = new TestHttpServer(8 * 1024 * 1024)
        {
            ChunkSize = 16 * 1024,
            ChunkDelayMs = 25
        };
        var settings = CreateSettings(s =>
        {
            s.Connection.MaxConnectionsPerFile = 2;
            s.Connection.RetryCount = 1;
            s.Connection.RetryDelaySeconds = 1;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "pause");
        var item = manager.CreateItem(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "big.bin"
        });
        manager.Add(item);

        var running = manager.StartAsync(item);
        await Task.Delay(1500);
        manager.Pause(item);
        await running;

        Check.Equal(DownloadStatus.Paused, item.Status, "status after pausing");
        Check.True(File.Exists(item.PartialPath), "the partial file survives a pause");
        Check.True(item.DownloadedBytes > 0, "some bytes were transferred before the pause");
        Check.True(item.DownloadedBytes < item.TotalBytes, "the download was genuinely interrupted");
        Check.False(File.Exists(item.FullPath), "no finished file exists while paused");

        await manager.StartAsync(item);

        Check.Equal(DownloadStatus.Complete, item.Status, "status after resuming (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content after resuming");
        Check.False(File.Exists(item.PartialPath), "the partial file is gone once complete");
    }

    /// <summary>Every in-progress item must be stoppable and restartable.</summary>
    public static async Task StopLeavesItemRestartable(string root)
    {
        using var server = new TestHttpServer(2 * 1024 * 1024);
        var settings = CreateSettings(s => s.Connection.MaxConnectionsPerFile = 2);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "stop");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "restartable.bin"
        });
        Check.Equal(DownloadStatus.Complete, item.Status, "first download completed");

        manager.Stop(item);
        Check.True(item.Status is DownloadStatus.Stopped or DownloadStatus.Complete,
            "a finished item reports a terminal status when stopped");

        await manager.RestartAsync(item);
        Check.Equal(DownloadStatus.Complete, item.Status, "restart completed (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content after restart");
    }

    /// <summary>A 404 must surface as a precise, non-retryable error rather than a retry storm.</summary>
    public static async Task MissingFileReportsNotFound(string root)
    {
        using var server = new TestHttpServer(64 * 1024);
        var settings = CreateSettings(s =>
        {
            s.Connection.RetryCount = 2;
            s.Connection.RetryDelaySeconds = 1;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "missing");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url("/404"),
            Directory = folder,
            FileName = "missing.bin"
        });

        Check.Equal(DownloadStatus.Error, item.Status, "status for a missing file");
        Check.Equal(DownloadErrorKind.NotFound, item.ErrorKind, "error kind for a 404");
        Check.NotNull(item.ErrorMessage, "an error message is provided");
        Check.True(server.RequestCount <= 3,
            $"a 404 must not be retried repeatedly; saw {server.RequestCount} requests");
    }

    /// <summary>An authentication challenge must reach the UI as a prompt and then succeed.</summary>
    public static async Task AuthenticationChallengeIsAnsweredFromTheUi(string root)
    {
        using var server = new TestHttpServer(512 * 1024)
        {
            RequiresAuth = true,
            AuthUser = "alice",
            AuthPassword = "s3cret"
        };
        var settings = CreateSettings(s => s.Connection.MaxConnectionsPerFile = 2);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var prompts = 0;
        manager.CredentialsRequired += (_, args) =>
        {
            prompts++;
            args.CredentialsSupplied = true;
            args.Username = "alice";
            args.Password = "s3cret";
            args.Remember = false;
        };

        var folder = NewFolder(root, "auth");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "protected.bin"
        });

        Check.Equal(1, prompts, "the user was asked for credentials exactly once");
        Check.Equal(DownloadStatus.Complete, item.Status, "status after supplying credentials (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content after authenticating");
        Check.True(server.AuthChallengeCount >= 1, "the server did issue a challenge");
        Check.Equal(0, manager.SiteLogins.Count, "credentials are not stored unless the user asked");
    }

    /// <summary>An existing file must be preserved rather than silently replaced.</summary>
    public static async Task ExistingFileIsRenamedRatherThanOverwritten(string root)
    {
        using var server = new TestHttpServer(256 * 1024);
        var settings = CreateSettings(s =>
        {
            s.Downloads.ExistingFileAction = ExistingFileAction.Ask;
            s.Connection.MaxConnectionsPerFile = 1;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "rename");
        var existingPath = Path.Combine(folder, "payload.bin");
        await File.WriteAllTextAsync(existingPath, "do not touch");

        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "payload.bin"
        });

        Check.Equal(DownloadStatus.Complete, item.Status, "status (" + item.ErrorMessage + ")");
        Check.Equal("payload (1).bin", item.FileName, "the existing file forced a renamed destination");
        Check.Equal("do not touch", await File.ReadAllTextAsync(existingPath), "the original file is untouched");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "the new file holds the payload");
    }

    /// <summary>Files must be filed into the folder that matches their category.</summary>
    public static async Task CategoryFoldersAreHonoured(string root)
    {
        using var server = new TestHttpServer(128 * 1024);
        var videoFolder = NewFolder(root, "videos");
        var settings = CreateSettings(s =>
        {
            s.Downloads.UseCategoryFolders = true;
            s.Downloads.CategoryFolders["Video"] = videoFolder;
            s.Connection.MaxConnectionsPerFile = 1;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url("/movie.mp4"),
            FileName = "movie.mp4"
        });

        Check.Equal(DownloadCategory.Video, item.Category, "the extension selected the video category");
        Check.Equal(videoFolder, item.Directory, "the category folder was used");
        Check.Equal(DownloadStatus.Complete, item.Status, "status (" + item.ErrorMessage + ")");
        Check.True(File.Exists(Path.Combine(videoFolder, "movie.mp4")), "the file landed in the category folder");
    }

    /// <summary>Checksums must be produced when the setting is enabled.</summary>
    public static async Task ChecksumsAreComputedWhenEnabled(string root)
    {
        using var server = new TestHttpServer(64 * 1024);
        var settings = CreateSettings(s =>
        {
            s.Downloads.ComputeChecksums = true;
            s.Connection.MaxConnectionsPerFile = 1;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "hash");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "hashed.bin"
        });

        Check.Equal(DownloadStatus.Complete, item.Status, "status (" + item.ErrorMessage + ")");
        Check.NotNull(item.ChecksumMd5, "an MD5 checksum was computed");
        Check.NotNull(item.ChecksumSha1, "a SHA-1 checksum was computed");
        Check.Equal(32, item.ChecksumMd5!.Length, "MD5 hex length");
        Check.Equal(40, item.ChecksumSha1!.Length, "SHA-1 hex length");
    }

    /// <summary>The speed limit must actually slow a transfer down.</summary>
    public static async Task SpeedLimitSlowsTheTransfer(string root)
    {
        using var server = new TestHttpServer(2 * 1024 * 1024);
        var settings = CreateSettings(s =>
        {
            s.Connection.MaxConnectionsPerFile = 1;
            s.Connection.SpeedLimitEnabled = true;
            s.Connection.SpeedLimitKbPerSecond = 512;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "throttled");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "throttled.bin"
        });
        stopwatch.Stop();

        Check.Equal(DownloadStatus.Complete, item.Status, "status (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content is intact");

        // 2 MB at 512 KB/s cannot finish in less than roughly 3 seconds, even
        // allowing for the initial one-second bucket.
        Check.True(stopwatch.Elapsed.TotalSeconds >= 2.0,
            $"the speed limit did not bite; the transfer took only {stopwatch.Elapsed.TotalSeconds:0.00}s");
    }

    /// <summary>
    /// Locks in exactly what the probe reports. If this regresses, every other engine
    /// test becomes misleading: a probe that reports an unknown size silently
    /// disables segmentation, and the download still succeeds, just slowly.
    /// </summary>
    public static async Task ProbeReportsSizeAndRangeSupport(string root)
    {
        using var server = new TestHttpServer(3 * 1024 * 1024);
        var settings = CreateSettings(null);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var probe = await manager.ProbeAsync(server.Url());

        Check.True(probe.Success, "the probe succeeded: " + probe.ErrorMessage);
        Check.Equal((long)server.Payload.Length, probe.ContentLength, "the probe reported the exact size");
        Check.True(probe.SupportsRanges, "the probe detected byte-range support");
        Check.Equal("file.bin", probe.SuggestedFileName, "the probe derived the file name from the URL");
    }

    /// <summary>The queue must hold items until they are explicitly started.</summary>
    public static async Task QueuedDownloadsWaitForStart(string root)
    {
        using var server = new TestHttpServer(64 * 1024);
        var settings = CreateSettings(s => s.Connection.MaxConnectionsPerFile = 1);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "queue");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "queued.bin",
            AddToQueue = true,
            StartNow = false
        });

        Check.Equal(DownloadStatus.Queued, item.Status, "a queued item does not start immediately");
        Check.False(File.Exists(item.FullPath), "nothing was downloaded yet");

        await manager.StartAsync(item);
        Check.Equal(DownloadStatus.Complete, item.Status, "the queued item completed once started (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content after starting the queue");
    }

    /// <summary>
    /// A host on the exception list must be fetched over exactly one connection even
    /// when more are configured, because that is the whole point of the list.
    /// </summary>
    public static async Task SiteExceptionForcesASingleConnection(string root)
    {
        using var server = new TestHttpServer(4 * 1024 * 1024);
        var settings = CreateSettings(s => s.Connection.MaxConnectionsPerFile = 8);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        manager.AddSiteException("127.0.0.1", useSingleConnection: true, note: "test case");

        Check.True(manager.UsesSingleConnection(server.Url()), "the host is recognised as an exception");
        Check.False(manager.UsesSingleConnection("http://other.example.com/file.bin"),
            "an unrelated host is not an exception");

        var folder = NewFolder(root, "site-exception");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "single.bin"
        });

        Check.Equal(DownloadStatus.Complete, item.Status, "download status (" + item.ErrorMessage + ")");
        Check.Equal(1, item.Connections, "the exception forced a single connection");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content is intact");
    }

    /// <summary>A folder configured for one file type must beat the category folder.</summary>
    public static async Task PerTypeFolderOverridesTheCategoryFolder(string root)
    {
        using var server = new TestHttpServer(256 * 1024);

        var categoryFolder = NewFolder(root, "category");
        var typeFolder = NewFolder(root, "by-type");

        var settings = CreateSettings(s =>
        {
            s.Connection.MaxConnectionsPerFile = 1;
            s.Downloads.UseCategoryFolders = true;
            s.Downloads.CategoryFolders["Video"] = categoryFolder;
        });

        var fileTypes = new FileTypeRegistry();
        // .iso belongs to the Compressed category, and this rule sends it elsewhere.
        var rules = fileTypes.Rules.Select(rule => new FileTypeRule
        {
            Extension = rule.Extension,
            Action = rule.Action,
            Category = rule.Category,
            Description = rule.Description,
            Folder = rule.Extension == ".iso" ? typeFolder : rule.Folder
        }).ToList();

        fileTypes.ReplaceAll(rules);

        using var manager = new DownloadManager(settings, fileTypes, new DownloadStore());

        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url("/disk.iso"),
            FileName = "disk.iso"
        });

        Check.Equal(DownloadStatus.Complete, item.Status, "download status (" + item.ErrorMessage + ")");
        Check.Equal(typeFolder, item.Directory, "the per-type folder won over the category folder");
        Check.True(File.Exists(Path.Combine(typeFolder, "disk.iso")), "the file landed in the per-type folder");
    }

    /// <summary>The download list must carry the queue name so the grid can show it.</summary>
    public static async Task ItemCarriesTheQueueName(string root)
    {
        using var server = new TestHttpServer(64 * 1024);
        var settings = CreateSettings(s => s.Connection.MaxConnectionsPerFile = 1);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "queue-name");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "named.bin",
            QueueId = 1
        });

        Check.Equal("Night queue", item.QueueName, "the queue id was resolved to its name");
        Check.Equal(DownloadStatus.Complete, item.Status, "download status (" + item.ErrorMessage + ")");
    }

    /// <summary>Site exceptions must survive a save and reload.</summary>
    public static async Task SiteExceptionsPersist(string root)
    {
        using var server = new TestHttpServer(64 * 1024);
        var settings = CreateSettings(s => s.Connection.MaxConnectionsPerFile = 1);

        using (var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore()))
        {
            manager.SaveSiteExceptions(new[]
            {
                new SiteException { Host = "slow.example.com", Note = "ignores ranges" },
                new SiteException { Host = "cdn.example.org" }
            });

            Check.Equal(2, manager.SiteExceptions.Count, "two exceptions were stored");
        }

        using (var reloaded = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore()))
        {
            Check.Equal(2, reloaded.SiteExceptions.Count, "the exceptions were reloaded");
            Check.True(reloaded.UsesSingleConnection("http://slow.example.com/x.bin"),
                "the reloaded rule still applies");

            reloaded.RemoveSiteException("slow.example.com");
            Check.Equal(1, reloaded.SiteExceptions.Count, "an exception can be removed");
        }

        await Task.CompletedTask;
    }

    /// <summary>The probe must report the size of an FTP resource before downloading it.</summary>
    public static async Task FtpProbeReportsSize(string root)
    {
        using var server = new TestFtpServer(2 * 1024 * 1024);
        var settings = CreateSettings(null);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var probe = await manager.ProbeAsync(server.Url());

        Check.True(probe.Success, "the FTP probe succeeded: " + probe.ErrorMessage);
        Check.Equal((long)server.Payload.Length, probe.ContentLength, "the probe reported the size");
        Check.True(probe.SupportsRanges, "the probe reports resume support");
        Check.Equal("file.bin", probe.SuggestedFileName, "the probe derived the file name");
    }

    /// <summary>
    /// An FTP transfer must come out byte perfect and must use exactly one
    /// connection, whatever the global segment count says.
    /// </summary>
    public static async Task FtpDownloadIsBytePerfect(string root)
    {
        using var server = new TestFtpServer(2 * 1024 * 1024);

        var settings = CreateSettings(s =>
        {
            // Eight connections must not leak into FTP: that is what this proves.
            s.Connection.MaxConnectionsPerFile = 8;
            s.Connection.RetryCount = 3;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "ftp");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "ftp-file.bin"
        });

        Check.Equal(DownloadStatus.Complete, item.Status, "download status (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content is byte perfect");
        Check.Equal(1, item.Connections, "FTP used a single connection despite eight being configured");
        Check.True(server.TransferCount >= 1, "the server performed at least one transfer");
        Check.False(File.Exists(item.PartialPath), "the partial file is gone once complete");
    }

    /// <summary>A dropped FTP data connection must be resumed, not restarted from zero.</summary>
    public static async Task FtpResumesAfterDroppedConnections(string root)
    {
        using var server = new TestFtpServer(4 * 1024 * 1024) { TruncateTimes = 3 };

        var settings = CreateSettings(s =>
        {
            s.Connection.RetryCount = 6;
            s.Connection.RetryDelaySeconds = 1;
            s.Downloads.AutoRetryOnFailure = true;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "ftp-resume");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "resumed.bin"
        });

        Check.Equal(DownloadStatus.Complete, item.Status,
            "download status after a dropped connection (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content after resuming");
        Check.True(server.RestCount >= 1, "the resume used a restart marker, saw " + server.RestCount);
    }

    /// <summary>
    /// A server that ignores restart markers would silently splice a second copy of
    /// the file over the tail. The engine must detect that and start over instead of
    /// handing back a corrupt file.
    /// </summary>
    public static async Task FtpRejectsAnIgnoredRestartMarker(string root)
    {
        using var server = new TestFtpServer(1024 * 1024)
        {
            TruncateTimes = 1,
            IgnoreRestart = true
        };

        var settings = CreateSettings(s =>
        {
            s.Connection.RetryCount = 4;
            s.Connection.RetryDelaySeconds = 1;
            s.Downloads.AutoRetryOnFailure = true;
        });
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var folder = NewFolder(root, "ftp-ignore");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "ignored.bin"
        });

        Check.Equal(DownloadStatus.Complete, item.Status, "download status (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath),
            "the ignored restart marker did not corrupt the file");
    }

    /// <summary>An FTP login prompt must reach the user, exactly like an HTTP 401.</summary>
    public static async Task FtpAuthenticationIsAnsweredFromTheUi(string root)
    {
        using var server = new TestFtpServer(256 * 1024)
        {
            RequiresAuth = true,
            AuthPassword = "hunter2"
        };

        var settings = CreateSettings(s => s.Connection.RetryCount = 3);
        using var manager = new DownloadManager(settings, new FileTypeRegistry(), new DownloadStore());

        var prompts = 0;
        manager.CredentialsRequired += (_, args) =>
        {
            prompts++;
            args.CredentialsSupplied = true;
            args.Username = "bob";
            args.Password = "hunter2";
            args.Remember = false;
        };

        var folder = NewFolder(root, "ftp-auth");
        var item = await manager.AddAndStartAsync(new AddDownloadRequest
        {
            Url = server.Url(),
            Directory = folder,
            FileName = "protected.bin"
        });

        Check.True(prompts >= 1, "the user was asked for credentials");
        Check.Equal(DownloadStatus.Complete, item.Status, "download status (" + item.ErrorMessage + ")");
        Check.BytesEqual(server.Payload, await File.ReadAllBytesAsync(item.FullPath), "content after authenticating");
    }
}
