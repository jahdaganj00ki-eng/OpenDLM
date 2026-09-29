using System.Reflection;

namespace OpenDLM.Core.Util;

/// <summary>
/// Central definition of every path OpenDLM writes to.
/// State lives under %APPDATA%\OpenDLM so it roams with the user profile,
/// while logs and caches live under %LOCALAPPDATA%\OpenDLM because they are
/// machine specific.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "OpenDLM";
    public const string PipeName = "OpenDLM.ipc";

    /// <summary>Native messaging host name registered with Chromium browsers.</summary>
    public const string NativeHostName = "com.opendlm.host";

    /// <summary>
    /// Optional portable-mode root. When the OPEN_DLM_HOME environment variable is
    /// set, every piece of state lives inside it, so OpenDLM can run from a USB
    /// stick with no trace in the user profile — and the test suite can run
    /// hermetically instead of writing into the real settings.
    /// </summary>
    public static string? PortableRoot
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("OPEN_DLM_HOME");
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
        }
    }

    public static string RoamingRoot { get; } = EnsureDir(PortableRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName));

    public static string LocalRoot { get; } = EnsureDir(PortableRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName));

    public static string LogDirectory { get; } = EnsureDir(Path.Combine(LocalRoot, "logs"));

    /// <summary>Directory that holds partially downloaded files and their resume state.</summary>
    public static string TempDirectory { get; } = EnsureDir(Path.Combine(RoamingRoot, "incomplete"));

    public static string SettingsFile => Path.Combine(RoamingRoot, "settings.json");

    public static string DownloadsFile => Path.Combine(RoamingRoot, "downloads.json");

    public static string QueuesFile => Path.Combine(RoamingRoot, "queues.json");

    public static string SitesLoginsFile => Path.Combine(RoamingRoot, "sites-logins.json");

    /// <summary>Hosts that must use a single connection, plus any other per-site rule.</summary>
    public static string SitesExceptionsFile => Path.Combine(RoamingRoot, "sites-exceptions.json");

    public static string FileTypesFile => Path.Combine(RoamingRoot, "file-types.json");

    public static string LogFile => Path.Combine(LogDirectory, "opendlm.log");

    public static string NativeHostManifestFile => Path.Combine(LocalRoot, "com.opendlm.host.json");

    /// <summary>Default download folder: the user's Downloads library.</summary>
    public static string DefaultDownloadDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string ApplicationDirectory =>
        Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppContext.BaseDirectory;

    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (!string.IsNullOrWhiteSpace(info?.InformationalVersion))
            {
                // Strip the "+<commit hash>" suffix the SDK appends.
                var value = info.InformationalVersion;
                var plus = value.IndexOf('+');
                return plus > 0 ? value[..plus] : value;
            }
            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    private static string EnsureDir(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // A read-only profile must not crash the app at static-init time.
            // Callers that actually need to write will surface a real error.
        }
        return path;
    }
}
