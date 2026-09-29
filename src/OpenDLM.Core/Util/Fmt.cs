using System.Globalization;

namespace OpenDLM.Core.Util;

/// <summary>Human readable formatting helpers shared by the engine, the CLI and the UI.</summary>
public static class Fmt
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Formats a byte count the way download managers conventionally do, e.g. "14.6 MB".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "?";
        }
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture)
               + " " + Units[unit];
    }

    /// <summary>Formats bytes-per-second, e.g. "2.31 MB/sec".</summary>
    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond < 0)
        {
            return string.Empty;
        }
        return Bytes((long)Math.Round(bytesPerSecond)) + "/sec";
    }

    /// <summary>Formats a duration as "01:23:45" or "12:34", matching download-manager conventions.</summary>
    public static string Duration(TimeSpan? value)
    {
        if (value is null || value.Value < TimeSpan.Zero)
        {
            return "Unknown";
        }

        var t = value.Value;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Formats an elapsed duration as "3 min 12 sec" for the status bar.</summary>
    public static string Elapsed(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours} h {value.Minutes} min";
        }
        if (value.TotalMinutes >= 1)
        {
            return $"{(int)value.TotalMinutes} min {value.Seconds} sec";
        }
        return $"{value.Seconds} sec";
    }

    /// <summary>Truncates long URLs/descriptions for grid display.</summary>
    public static string Ellipsis(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "\u2026";
    }

    /// <summary>Percentage in [0,100], or 0 when the total size is unknown.</summary>
    public static double Percent(long downloaded, long total)
        => total <= 0 ? 0 : Math.Clamp(downloaded * 100.0 / total, 0, 100);
}
