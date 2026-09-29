using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Cli;

/// <summary>
/// Parses the command line.
///
/// Both the Unix style (<c>-d URL</c>, <c>--download URL</c>) and the Windows style
/// used by most download managers (<c>/d URL</c>) are accepted, because browser
/// integrations and .url shortcut files in the wild use either form.
/// </summary>
public sealed class CliOptions
{
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }
    public bool Silent { get; set; }
    public bool StartMinimized { get; set; }
    public bool ExitAfterDownload { get; set; }
    public string? BatchFile { get; set; }
    public List<string> Errors { get; } = new();
    public List<AddDownloadRequest> Requests { get; } = new();

    public bool HasWork => Requests.Count > 0 || !string.IsNullOrWhiteSpace(BatchFile);

    public static string HelpText => """
        OpenDLM - free download manager
        Usage: OpenDLM [options]

        Download options
          -d, --download <url>     URL to download (repeatable)
          -p, --path <folder>      Destination folder
          -f, --filename <name>    Destination file name (applies to the previous URL)
          -q, --queue              Add to the queue instead of starting immediately
          -b, --batch <file>       Text file with one URL per line ('#' starts a comment)
          -r, --referer <url>      Referer to send
          -u, --username <user>    User name for HTTP authentication
          -w, --password <pass>    Password for HTTP authentication
          -c, --connections <n>    Simultaneous connections for this download (1-32)
              --cookie <value>     Cookie header to send
              --agent <value>      User-Agent to send
              --description <text> Description shown in the list
              --category <name>    video | music | program | document | compressed | other

        Application options
          -s, --silent             Do not show the main window
          -m, --minimized          Start minimized to the notification area
          -x, --exit-after         Exit once every download has finished
          -h, --help               Show this help
          -v, --version            Show the version

        Examples
          OpenDLM -d https://example.com/big.iso
          OpenDLM -d https://example.com/a.zip -p D:\Downloads -f archive.zip -c 16
          OpenDLM --batch urls.txt --queue
        """;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        AddDownloadRequest? pending = null;

        void Commit()
        {
            if (pending is not null)
            {
                options.Requests.Add(pending);
                pending = null;
            }
        }

        for (var index = 0; index < args.Length; index++)
        {
            var raw = args[index];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Normalize "/d", "-d" and "--download" to a bare switch name.
            var token = raw.Trim();
            var value = (string?)null;

            var equals = token.IndexOf('=');
            if (equals > 1 && token.StartsWith("-", StringComparison.Ordinal))
            {
                value = token[(equals + 1)..];
                token = token[..equals];
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                token = token[2..];
            }
            else if (token.StartsWith("-", StringComparison.Ordinal) || token.StartsWith("/", StringComparison.Ordinal))
            {
                token = token[1..];
            }
            else
            {
                // A bare argument is treated as a URL when it looks like one.
                if (LooksLikeUrl(token))
                {
                    Commit();
                    pending = new AddDownloadRequest { Url = token };
                    continue;
                }

                options.Errors.Add($"Unrecognized argument: {raw}");
                continue;
            }

            var name = token.ToLowerInvariant();

            string? TakeValue()
            {
                if (value is not null)
                {
                    return value;
                }
                if (index + 1 < args.Length && !IsSwitch(args[index + 1]))
                {
                    return args[++index];
                }
                return null;
            }

            switch (name)
            {
                case "d" or "download" or "url":
                {
                    var url = TakeValue();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        options.Errors.Add("Missing value for --download.");
                        break;
                    }

                    // Fold the URL into a pending entry that only carries options so
                    // far, which makes both "-p DIR -d URL" and "-d URL -p DIR" work.
                    if (pending is not null && string.IsNullOrWhiteSpace(pending.Url))
                    {
                        pending.Url = url.Trim();
                    }
                    else
                    {
                        Commit();
                        pending = new AddDownloadRequest { Url = url.Trim() };
                    }
                    break;
                }

                case "p" or "path" or "dir" or "folder":
                {
                    var path = TakeValue();
                    // Attach to the current entry rather than starting a new one;
                    // otherwise "-d URL -p DIR" would split the arguments across two
                    // entries and drop the folder.
                    pending ??= new AddDownloadRequest();
                    pending.Directory = path;
                    break;
                }

                case "f" or "filename" or "out":
                {
                    var fileName = TakeValue();
                    if (pending is null)
                    {
                        pending = new AddDownloadRequest { FileName = fileName };
                    }
                    else
                    {
                        pending.FileName = fileName;
                    }
                    break;
                }

                case "r" or "referer" or "referrer":
                    pending ??= new AddDownloadRequest();
                    pending.Referer = TakeValue();
                    break;

                case "u" or "username" or "user":
                    pending ??= new AddDownloadRequest();
                    pending.Username = TakeValue();
                    break;

                case "w" or "password" or "pass":
                    pending ??= new AddDownloadRequest();
                    pending.Password = TakeValue();
                    break;

                case "c" or "connections" or "segments":
                    pending ??= new AddDownloadRequest();
                    if (int.TryParse(TakeValue(), out var connections))
                    {
                        pending.Connections = Math.Clamp(connections, 1, 32);
                    }
                    break;

                case "cookie" or "cookies":
                    pending ??= new AddDownloadRequest();
                    pending.Cookies = TakeValue();
                    break;

                case "agent" or "useragent":
                    pending ??= new AddDownloadRequest();
                    pending.UserAgent = TakeValue();
                    break;

                case "description" or "desc":
                    pending ??= new AddDownloadRequest();
                    pending.Description = TakeValue();
                    break;

                case "category":
                {
                    pending ??= new AddDownloadRequest();
                    pending.Category = ParseCategory(TakeValue());
                    break;
                }

                case "q" or "queue":
                    pending ??= new AddDownloadRequest();
                    pending.AddToQueue = true;
                    pending.StartNow = false;
                    break;

                case "b" or "batch" or "batchfile":
                    options.BatchFile = TakeValue();
                    break;

                case "s" or "silent" or "noshow":
                    options.Silent = true;
                    break;

                case "m" or "minimized" or "minimize":
                    options.StartMinimized = true;
                    options.Silent = true;
                    break;

                case "x" or "exit-after" or "exitafter":
                    options.ExitAfterDownload = true;
                    break;

                case "h" or "help" or "?":
                    options.ShowHelp = true;
                    break;

                case "v" or "version":
                    options.ShowVersion = true;
                    break;

                default:
                    options.Errors.Add($"Unrecognized option: {raw}");
                    break;
            }
        }

        Commit();

        // Drop entries that carry only a folder and no URL.
        options.Requests.RemoveAll(r => string.IsNullOrWhiteSpace(r.Url));

        if (!string.IsNullOrWhiteSpace(options.BatchFile) && !File.Exists(options.BatchFile))
        {
            options.Errors.Add($"Batch file not found: {options.BatchFile}");
        }

        return options;
    }

    /// <summary>Expands a batch file into individual requests, ignoring blank lines and comments.</summary>
    public List<AddDownloadRequest> ExpandBatchFile()
    {
        var list = new List<AddDownloadRequest>(Requests);

        if (string.IsNullOrWhiteSpace(BatchFile) || !File.Exists(BatchFile))
        {
            return list;
        }

        try
        {
            foreach (var line in File.ReadAllLines(BatchFile))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal) ||
                    trimmed.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                list.Add(new AddDownloadRequest
                {
                    Url = trimmed,
                    Description = "From batch file " + Path.GetFileName(BatchFile),
                    AddToQueue = true,
                    StartNow = false
                });
            }
        }
        catch (Exception ex)
        {
            Errors.Add($"Could not read the batch file: {ex.Message}");
        }

        return list;
    }

    private static bool IsSwitch(string value)
        => value.Length > 0 && (value[0] == '-' || value[0] == '/');

    private static bool LooksLikeUrl(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    private static DownloadCategory? ParseCategory(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "video" or "videos" => DownloadCategory.Video,
        "music" or "audio" => DownloadCategory.Music,
        "program" or "programs" or "software" => DownloadCategory.Program,
        "document" or "documents" or "docs" => DownloadCategory.Document,
        "compressed" or "archive" or "archives" => DownloadCategory.Compressed,
        "other" => DownloadCategory.Other,
        _ => null
    };
}
