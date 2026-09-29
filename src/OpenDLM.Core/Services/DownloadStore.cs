using System.Text.Json;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>
/// Persists the download list (including resume state), the named queues and the
/// stored site logins, so the application reopens exactly where the user left it.
/// </summary>
public sealed class DownloadStore
{
    private readonly object _gate = new();

    /// <summary>Serializes the download list. Called on a timer, not on every byte.</summary>
    public void SaveDownloads(IEnumerable<DownloadItem> items)
    {
        try
        {
            List<DownloadItem> snapshot;
            lock (_gate)
            {
                snapshot = items.ToList();
            }

            SettingsService.AtomicWrite(AppPaths.DownloadsFile,
                JsonSerializer.Serialize(snapshot, SettingsService.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save the download list.", ex);
        }
    }

    public List<DownloadItem> LoadDownloads()
    {
        try
        {
            if (!File.Exists(AppPaths.DownloadsFile))
            {
                return new List<DownloadItem>();
            }

            var json = File.ReadAllText(AppPaths.DownloadsFile);
            var items = JsonSerializer.Deserialize<List<DownloadItem>>(json, SettingsService.JsonOptions)
                        ?? new List<DownloadItem>();

            foreach (var item in items)
            {
                // Anything that claimed to be running when the app died is now stopped.
                if (item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting
                    or DownloadStatus.Finalizing or DownloadStatus.Queued)
                {
                    item.Status = item.DownloadedBytes > 0 ? DownloadStatus.Paused : DownloadStatus.Stopped;
                }
                item.Speed = 0;
                item.TimeLeft = null;
            }

            Log.Info($"Loaded {items.Count} download(s) from the previous session.");
            return items;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load the download list.", ex);
            Quarantine(AppPaths.DownloadsFile);
            return new List<DownloadItem>();
        }
    }

    public void SaveQueues(IEnumerable<DownloadQueue> queues)
    {
        try
        {
            var snapshot = queues.ToList();
            SettingsService.AtomicWrite(AppPaths.QueuesFile,
                JsonSerializer.Serialize(snapshot, SettingsService.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save queues.", ex);
        }
    }

    public List<DownloadQueue> LoadQueues()
    {
        try
        {
            if (!File.Exists(AppPaths.QueuesFile))
            {
                return DefaultQueues();
            }

            var json = File.ReadAllText(AppPaths.QueuesFile);
            var queues = JsonSerializer.Deserialize<List<DownloadQueue>>(json, SettingsService.JsonOptions);
            if (queues is not { Count: > 0 })
            {
                return DefaultQueues();
            }

            // The default queue is implicit and must always exist.
            if (queues.All(q => q.Id != 0))
            {
                queues.Insert(0, new DownloadQueue { Id = 0, Name = "Main queue" });
            }
            return queues.OrderBy(q => q.Id).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load queues; using defaults.", ex);
            return DefaultQueues();
        }
    }

    public static List<DownloadQueue> DefaultQueues() => new()
    {
        new DownloadQueue { Id = 0, Name = "Main queue", Enabled = true, Scheduled = false },
        new DownloadQueue
        {
            Id = 1,
            Name = "Night queue",
            Enabled = true,
            Scheduled = true,
            StartTime = new TimeSpan(23, 0, 0),
            StopTime = new TimeSpan(7, 0, 0),
            Days = 0x7F
        }
    };

    // ------------------------------------------------------------- site logins

    public void SaveSiteLogins(IEnumerable<SiteLogin> logins)
    {
        try
        {
            var snapshot = logins.ToList();
            SettingsService.AtomicWrite(AppPaths.SitesLoginsFile,
                JsonSerializer.Serialize(snapshot, SettingsService.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save site logins.", ex);
        }
    }

    public List<SiteLogin> LoadSiteLogins()
    {
        try
        {
            if (!File.Exists(AppPaths.SitesLoginsFile))
            {
                return new List<SiteLogin>();
            }

            var json = File.ReadAllText(AppPaths.SitesLoginsFile);
            var logins = JsonSerializer.Deserialize<List<SiteLogin>>(json, SettingsService.JsonOptions)
                         ?? new List<SiteLogin>();

            foreach (var login in logins)
            {
                login.PlainPassword = CredentialProtector.Unprotect(login.PasswordProtected);
            }
            return logins;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load site logins.", ex);
            return new List<SiteLogin>();
        }
    }

    private static void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not quarantine " + path + ": " + ex.Message);
        }
    }
}
