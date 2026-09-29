using System.Text;

namespace OpenDLM.NativeHost;

/// <summary>
/// Diagnostics for the native messaging host.
///
/// This logger writes to a file only. It must never touch stdout, because
/// stdout carries the native messaging protocol frames: a single stray line
/// of text would corrupt the stream that the browser is parsing.
///
/// The file is kept under %LOCALAPPDATA%\OpenDLM\logs\nativehost.log and is
/// trimmed once it passes the cap, so a long-running browser session cannot
/// fill the disk.
/// </summary>
internal static class Log
{
    /// <summary>Trim the log once it grows past this size.</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>How much of the tail to keep when trimming.</summary>
    private const int KeepTailBytes = 256 * 1024;

    private static readonly object Gate = new();

    private static string? _filePath;

    /// <summary>Absolute path of the host log file.</summary>
    public static string FilePath => _filePath ??= BuildPath();

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message) => Write("WARN ", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void Debug(string message) => Write("DEBUG", message, null);

    private static string BuildPath()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
            {
                local = Path.GetTempPath();
            }

            var directory = Path.Combine(local, "OpenDLM", "logs");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "nativehost.log");
        }
        catch
        {
            // A locked-down profile must not stop the host from running.
            try
            {
                return Path.Combine(Path.GetTempPath(), "opendlm-nativehost.log");
            }
            catch
            {
                return "opendlm-nativehost.log";
            }
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            builder.Append(" [").Append(level).Append("] ").Append(message);
            if (exception is not null)
            {
                builder.Append(" -- ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
                var stack = exception.StackTrace;
                if (!string.IsNullOrEmpty(stack))
                {
                    foreach (var line in stack.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        builder.Append(Environment.NewLine).Append("        ").Append(line.Trim());
                    }
                }
            }

            builder.Append(Environment.NewLine);

            lock (Gate)
            {
                var path = FilePath;
                TrimIfNeeded(path);
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging is strictly best effort.
        }
    }

    private static void TrimIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }

            var all = File.ReadAllBytes(path);
            var start = Math.Max(0, all.Length - KeepTailBytes);

            // Do not resume in the middle of a line.
            while (start < all.Length && all[start] != (byte)'\n')
            {
                start++;
            }

            if (start < all.Length)
            {
                start++;
            }

            var tail = new byte[all.Length - start];
            Array.Copy(all, start, tail, 0, tail.Length);
            File.WriteAllBytes(path, tail);
        }
        catch
        {
            // Trimming is best effort; the append above still succeeded.
        }
    }
}
