using System.IO.Compression;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>One entry of a ZIP archive, as the preview shows it.</summary>
public sealed record ZipEntryInfo(string Name, long Length, long CompressedLength, DateTimeOffset Modified)
{
    /// <summary>The ratio between stored and compressed size, 0 when not compressible.</summary>
    public int Ratio => CompressedLength <= 0
        ? 0
        : (int)Math.Clamp((1 - Length / (double)CompressedLength) * 100, 0, 100);
}

/// <summary>
/// Lists the contents of a ZIP file without extracting it, the reference's
/// "Zip preview".
///
/// It reads the central directory at the end of the file, so it works on an
/// archive that is still downloading, as long as the directory has arrived. That is
/// the point: deciding what is inside a large archive before waiting for all of it.
/// </summary>
public static class ZipPreview
{
    public static IReadOnlyList<ZipEntryInfo> Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<ZipEntryInfo>();
            }

            using var archive = ZipFile.OpenRead(path);

            return archive.Entries
                .Select(entry => new ZipEntryInfo(
                    entry.FullName,
                    entry.Length,
                    entry.CompressedLength,
                    entry.LastWriteTime))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (InvalidDataException)
        {
            // The archive is truncated, which is exactly the case during a download.
            Log.Info("The archive is not complete yet: " + path);
            return Array.Empty<ZipEntryInfo>();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the archive " + path + ": " + ex.Message);
            return Array.Empty<ZipEntryInfo>();
        }
    }

    /// <summary>What the preview can tell about the archive as a whole.</summary>
    public static string Summarise(IReadOnlyList<ZipEntryInfo> entries)
    {
        if (entries.Count == 0)
        {
            return "No entries could be read yet.";
        }

        var unpacked = entries.Sum(entry => entry.Length);
        var packed = entries.Sum(entry => entry.CompressedLength);
        var ratio = packed <= 0 ? 0 : (int)Math.Clamp((1 - unpacked / (double)packed) * 100, 0, 100);

        return $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}, " +
               $"{Fmt.Bytes(unpacked)} unpacked, {Fmt.Bytes(packed)} packed ({ratio} % saved).";
    }
}
