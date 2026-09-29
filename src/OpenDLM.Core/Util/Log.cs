namespace OpenDLM.Core.Util;

/// <summary>
/// Minimal, dependency-free, thread-safe file logger with size-based rotation.
/// Deliberately tiny: OpenDLM must never fail because logging failed.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;
    private static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message) => Write("WARN ", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static void Debug(string message) => Write("DEBUG", message, null);

    private static void Write(string level, string message, Exception? ex)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex is not null)
            {
                line += Environment.NewLine + "        " + ex.GetType().Name + ": " + ex.Message;
                if (ex.StackTrace is { Length: > 0 } trace)
                {
                    foreach (var frame in trace.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        line += Environment.NewLine + "        " + frame.Trim();
                    }
                }
            }

            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Never let diagnostics break the download engine.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(AppPaths.LogFile);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            var previous = AppPaths.LogFile + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }
            File.Move(AppPaths.LogFile, previous);
        }
        catch
        {
            // Rotation is best effort.
        }
    }

    /// <summary>Reads the tail of the log for the "View log" UI command.</summary>
    public static string Read(int maxLines = 500)
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile))
            {
                return string.Empty;
            }

            var lines = File.ReadAllLines(AppPaths.LogFile);
            if (lines.Length <= maxLines)
            {
                return string.Join(Environment.NewLine, lines);
            }
            return string.Join(Environment.NewLine, lines[^maxLines..]);
        }
        catch (Exception ex)
        {
            return "Unable to read log: " + ex.Message;
        }
    }
}
