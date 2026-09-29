using System.Text.Json;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>
/// Maps file extensions to a <see cref="DownloadCategory"/> and to a
/// <see cref="FileTypeAction"/>, and persists the user's edits to
/// <c>%APPDATA%\OpenDLM\file-types.json</c>.
///
/// The default table is what makes browser takeover "just work": the extension asks
/// OpenDLM for this list and claims only the types the user opted into.
/// </summary>
public sealed class FileTypeRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FileTypeRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public FileTypeRegistry()
    {
        Load();
    }

    /// <summary>Raised whenever a rule changes, so the extension can be told to refresh.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<FileTypeRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return _rules.Values.OrderBy(r => r.Extension, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    // ------------------------------------------------------------ default table

    private static readonly string[] VideoExtensions =
    {
        "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg",
        "ts", "m2ts", "3gp", "3g2", "vob", "rmvb", "divx", "ogv", "f4v", "asf"
    };

    private static readonly string[] MusicExtensions =
    {
        "mp3", "wav", "flac", "aac", "ogg", "oga", "wma", "m4a", "opus", "aiff",
        "aif", "mid", "midi", "ape", "wv", "mka"
    };

    private static readonly string[] ProgramExtensions =
    {
        "exe", "msi", "msp", "msu", "dmg", "pkg", "deb", "rpm", "apk", "appx",
        "msix", "jar", "bat", "cmd", "com", "gadget", "air", "xap", "ipa"
    };

    private static readonly string[] DocumentExtensions =
    {
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt",
        "ods", "odp", "epub", "mobi", "azw", "azw3", "djvu", "csv", "chm", "ps"
    };

    private static readonly string[] CompressedExtensions =
    {
        "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz", "xz", "txz", "zst",
        "iso", "cab", "arj", "lzh", "lha", "z", "ace", "sit", "sitx", "img", "nrg", "mdf"
    };

    /// <summary>Extensions that are deliberately never taken over from the browser.</summary>
    private static readonly string[] NeverTakeOver =
    {
        "htm", "html", "xhtml", "shtml", "php", "asp", "aspx", "jsp", "cgi", "do",
        "json", "xml", "css", "js", "mjs", "webmanifest", "svg", "ico", "woff", "woff2"
    };

    private static Dictionary<string, FileTypeRule> BuildDefaults()
    {
        var rules = new Dictionary<string, FileTypeRule>(StringComparer.OrdinalIgnoreCase);

        void AddRange(IEnumerable<string> extensions, DownloadCategory category, FileTypeAction action)
        {
            foreach (var extension in extensions)
            {
                var key = FileTypeRule.Normalize(extension);
                rules[key] = new FileTypeRule
                {
                    Extension = key,
                    Category = category,
                    Action = action,
                    Description = DescribeCategory(category)
                };
            }
        }

        AddRange(VideoExtensions, DownloadCategory.Video, FileTypeAction.TakeOver);
        AddRange(MusicExtensions, DownloadCategory.Music, FileTypeAction.TakeOver);
        AddRange(ProgramExtensions, DownloadCategory.Program, FileTypeAction.TakeOver);
        AddRange(DocumentExtensions, DownloadCategory.Document, FileTypeAction.Ask);
        AddRange(CompressedExtensions, DownloadCategory.Compressed, FileTypeAction.TakeOver);

        foreach (var extension in NeverTakeOver)
        {
            var key = FileTypeRule.Normalize(extension);
            rules[key] = new FileTypeRule
            {
                Extension = key,
                Category = DownloadCategory.Other,
                Action = FileTypeAction.DoNotTakeOver,
                Description = "Web page assets"
            };
        }

        return rules;
    }

    private static string DescribeCategory(DownloadCategory category) => category switch
    {
        DownloadCategory.Video => "Video",
        DownloadCategory.Music => "Music",
        DownloadCategory.Program => "Programs",
        DownloadCategory.Document => "Documents",
        DownloadCategory.Compressed => "Compressed",
        _ => "Other"
    };

    // ------------------------------------------------------------------ queries

    /// <summary>Categorises a file name or path by extension. Falls back to <see cref="DownloadCategory.Other"/>.</summary>
    public DownloadCategory CategoryOf(string? fileNameOrUrl)
    {
        var extension = ExtensionOf(fileNameOrUrl);
        if (extension.Length == 0)
        {
            return DownloadCategory.Other;
        }

        lock (_gate)
        {
            return _rules.TryGetValue(extension, out var rule) ? rule.Category : DownloadCategory.Other;
        }
    }

    /// <summary>Returns the configured takeover action for a file name or URL.</summary>
    public FileTypeAction ActionOf(string? fileNameOrUrl)
    {
        var extension = ExtensionOf(fileNameOrUrl);
        if (extension.Length == 0)
        {
            // Unknown type: let the user decide rather than guessing.
            return FileTypeAction.Ask;
        }

        lock (_gate)
        {
            return _rules.TryGetValue(extension, out var rule) ? rule.Action : FileTypeAction.Ask;
        }
    }

    /// <summary>Extensions the browser extension should claim, in "*.ext" form.</summary>
    public List<string> TakeOverMasks()
    {
        lock (_gate)
        {
            return _rules.Values
                .Where(r => r.Action == FileTypeAction.TakeOver)
                .Select(r => r.Mask)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Extensions the browser extension must always leave alone.</summary>
    public List<string> ExcludedMasks()
    {
        lock (_gate)
        {
            return _rules.Values
                .Where(r => r.Action == FileTypeAction.DoNotTakeOver)
                .Select(r => r.Mask)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static string ExtensionOf(string? fileNameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrUrl))
        {
            return string.Empty;
        }

        var value = fileNameOrUrl.Trim();

        // Try to reduce a URL to its last path segment first.
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.AbsolutePath;
        }
        else
        {
            var query = value.IndexOfAny(new[] { '?', '#' });
            if (query >= 0)
            {
                value = value[..query];
            }
        }

        var lastSlash = value.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            value = value[(lastSlash + 1)..];
        }

        var extension = Path.GetExtension(value);
        return extension.Length <= 1 ? string.Empty : extension.ToLowerInvariant();
    }

    // ------------------------------------------------------------------ mutation

    public void SetAction(string extension, FileTypeAction action, DownloadCategory? category = null)
    {
        var key = FileTypeRule.Normalize(extension);
        if (key.Length <= 1)
        {
            return;
        }

        lock (_gate)
        {
            if (!_rules.TryGetValue(key, out var rule))
            {
                rule = new FileTypeRule { Extension = key, Category = category ?? DownloadCategory.Other };
                _rules[key] = rule;
            }
            rule.Action = action;
            if (category.HasValue)
            {
                rule.Category = category.Value;
            }
        }
        Save();
    }

    public void SetCategory(string extension, DownloadCategory category)
    {
        var key = FileTypeRule.Normalize(extension);
        if (key.Length <= 1)
        {
            return;
        }

        lock (_gate)
        {
            if (!_rules.TryGetValue(key, out var rule))
            {
                rule = new FileTypeRule { Extension = key, Action = FileTypeAction.Ask };
                _rules[key] = rule;
            }
            rule.Category = category;
        }
        Save();
    }

    public bool Remove(string extension)
    {
        var key = FileTypeRule.Normalize(extension);
        bool removed;
        lock (_gate)
        {
            removed = _rules.Remove(key);
        }
        if (removed)
        {
            Save();
        }
        return removed;
    }

    /// <summary>Replaces the whole table (used by the options dialog after a bulk edit).</summary>
    public void ReplaceAll(IEnumerable<FileTypeRule> rules)
    {
        lock (_gate)
        {
            _rules.Clear();
            foreach (var rule in rules)
            {
                var key = FileTypeRule.Normalize(rule.Extension);
                if (key.Length > 1)
                {
                    rule.Extension = key;
                    _rules[key] = rule;
                }
            }
        }
        Save();
    }

    public void RestoreDefaults()
    {
        lock (_gate)
        {
            _rules.Clear();
            foreach (var pair in BuildDefaults())
            {
                _rules[pair.Key] = pair.Value;
            }
        }
        Save();
    }

    // --------------------------------------------------------------- persistence

    private void Load()
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(AppPaths.FileTypesFile))
                {
                    var json = File.ReadAllText(AppPaths.FileTypesFile);
                    var list = JsonSerializer.Deserialize<List<FileTypeRule>>(json, SettingsService.JsonOptions);
                    if (list is { Count: > 0 })
                    {
                        foreach (var rule in list)
                        {
                            var key = FileTypeRule.Normalize(rule.Extension);
                            if (key.Length > 1)
                            {
                                rule.Extension = key;
                                _rules[key] = rule;
                            }
                        }
                        return;
                    }
                }

                foreach (var pair in BuildDefaults())
                {
                    _rules[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load file types; using defaults.", ex);
            lock (_gate)
            {
                _rules.Clear();
                foreach (var pair in BuildDefaults())
                {
                    _rules[pair.Key] = pair.Value;
                }
            }
        }
    }

    public void Save()
    {
        try
        {
            List<FileTypeRule> snapshot;
            lock (_gate)
            {
                snapshot = _rules.Values
                    .OrderBy(r => r.Extension, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            SettingsService.AtomicWrite(AppPaths.FileTypesFile,
                JsonSerializer.Serialize(snapshot, SettingsService.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save file types.", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
