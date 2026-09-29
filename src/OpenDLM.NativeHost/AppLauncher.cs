using System.Diagnostics;
using Microsoft.Win32;

namespace OpenDLM.NativeHost;

/// <summary>
/// Finds and starts the OpenDLM desktop app when the IPC pipe is not up yet.
///
/// The host never fails because the app could not be found: it reports the
/// situation back to the extension instead, so the browser always gets an answer.
/// </summary>
internal static class AppLauncher
{
    private const string AppExecutableName = "OpenDLM.exe";
    private const string InstallKeyPath = @"Software\OpenDLM";
    private const string InstallValueName = "InstallPath";

    /// <summary>How many directory levels above the host to look for a source checkout.</summary>
    private const int MaxAncestorHops = 6;

    /// <summary>How long to keep retrying the pipe after a launch before giving up.</summary>
    public static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(8);

    /// <summary>Shorter budget used for the handshake so the popup stays responsive.</summary>
    public static readonly TimeSpan HandshakeLaunchWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Candidate locations for OpenDLM.exe, most authoritative first.
    /// Order: registry install path, a sibling copy, then developer build output.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths()
    {
        var candidates = new List<string>();

        AddCandidate(candidates, RegistryCandidate());
        AddCandidate(candidates, SiblingCandidate());

        // Developer convenience. The literal spec path is included for
        // completeness, plus a walk up the tree so a normal bin\Debug layout
        // (or bin\Release) is found regardless of how deep the host sits.
        AddCandidate(candidates, CombineFromBase("..", "..", "..", "..", "src", "OpenDLM.App",
            "bin", "Debug", "net8.0-windows", AppExecutableName));

        foreach (var ancestor in Ancestors())
        {
            AddCandidate(candidates, Path.Combine(ancestor, "src", "OpenDLM.App", "bin", "Debug", "net8.0-windows", AppExecutableName));
            AddCandidate(candidates, Path.Combine(ancestor, "src", "OpenDLM.App", "bin", "Release", "net8.0-windows", AppExecutableName));
            AddCandidate(candidates, Path.Combine(ancestor, "OpenDLM.App", "bin", "Debug", "net8.0-windows", AppExecutableName));
            AddCandidate(candidates, Path.Combine(ancestor, "OpenDLM.App", "bin", "Release", "net8.0-windows", AppExecutableName));
        }

        return candidates;
    }

    /// <summary>First candidate that actually exists on disk, or null.</summary>
    public static string? FindAppExecutable()
    {
        foreach (var candidate in CandidatePaths())
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // An unreadable path is simply not a match.
            }
        }

        return null;
    }

    /// <summary>
    /// Starts the app if it can be found. Returns false (without throwing) when
    /// it cannot be located or launched.
    /// </summary>
    public static bool TryLaunch(out string? launchedPath)
    {
        launchedPath = null;

        var path = FindAppExecutable();
        if (path is null)
        {
            Log.Warn("OpenDLM.exe was not found in any known location; not launching.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                startInfo.WorkingDirectory = directory;
            }

            using var process = Process.Start(startInfo);
            launchedPath = path;
            Log.Info("Launched OpenDLM from " + path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to launch OpenDLM from " + path, ex);
            return false;
        }
    }

    private static string? RegistryCandidate()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallKeyPath, writable: false);
            if (key?.GetValue(InstallValueName) is not string raw || string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var value = raw.Trim().Trim('"');
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return Path.Combine(value, AppExecutableName);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading HKCU\\" + InstallKeyPath + " failed: " + ex.Message);
            return null;
        }
    }

    private static string? SiblingCandidate()
    {
        try
        {
            return Path.Combine(AppContext.BaseDirectory, AppExecutableName);
        }
        catch (Exception ex)
        {
            Log.Warn("Resolving the host directory failed: " + ex.Message);
            return null;
        }
    }

    private static IEnumerable<string> Ancestors()
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(AppContext.BaseDirectory);
        }
        catch
        {
            yield break;
        }

        for (var hop = 0; hop < MaxAncestorHops && directory is not null; hop++)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private static string? CombineFromBase(params string[] parts)
    {
        try
        {
            var all = new string[parts.Length + 1];
            all[0] = AppContext.BaseDirectory;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.GetFullPath(Path.Combine(all));
        }
        catch
        {
            return null;
        }
    }

    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        foreach (var existing in candidates)
        {
            if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        candidates.Add(full);
    }
}
