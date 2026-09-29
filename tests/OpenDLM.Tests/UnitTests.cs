using System.Diagnostics;
using System.Text.Json;
using OpenDLM.Core.Cli;
using OpenDLM.Core.Http;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.Tests;

/// <summary>Fast, deterministic tests that need no network at all.</summary>
public static class UnitTests
{
    public static Task SegmentPlannerCoversFileExactly()
    {
        long[] sizes =
        {
            1, 1024, 1024 * 1024, 1024 * 1024 + 1, 5 * 1024 * 1024,
            10 * 1024 * 1024 + 7, 100 * 1024 * 1024
        };
        int[] connectionCounts = { 1, 2, 3, 4, 8, 16, 32 };

        foreach (var total in sizes)
        {
            foreach (var connections in connectionCounts)
            {
                var segments = SegmentPlanner.Create(total, connections);
                var label = $"total={total}, connections={connections}";

                Check.True(segments.Count >= 1, "at least one segment for " + label);
                Check.Equal(0L, segments[0].Start, "first segment starts at 0 for " + label);
                Check.Equal(total - 1, segments[^1].End, "last segment ends at the final byte for " + label);

                for (var index = 1; index < segments.Count; index++)
                {
                    Check.Equal(segments[index - 1].End + 1, segments[index].Start,
                        "segments are contiguous for " + label);
                }

                Check.Equal(total, segments.Sum(s => s.Length), "segments cover the file exactly for " + label);
                Check.True(segments.All(s => s.Position == s.Start), "segments start unwritten for " + label);
                Check.True(segments.All(s => !s.IsComplete), "fresh segments are incomplete for " + label);
                Check.Equal(total, SegmentPlanner.TotalOf(segments), "TotalOf agrees for " + label);
            }
        }

        return Task.CompletedTask;
    }

    public static Task SegmentPlannerHandlesUnknownSize()
    {
        var segments = SegmentPlanner.Create(-1, 8);
        Check.Equal(1, segments.Count, "an unknown-size download uses exactly one segment");
        Check.True(segments[0].End >= long.MaxValue - 1, "the single segment is open ended");
        Check.Equal(-1L, SegmentPlanner.TotalOf(segments), "an open-ended plan has no total");
        return Task.CompletedTask;
    }

    public static Task ByteFormattingIsReadable()
    {
        Check.Equal("512 B", Fmt.Bytes(512), "bytes");
        Check.Equal("1.00 KB", Fmt.Bytes(1024), "kilobytes");
        Check.Equal("1.50 MB", Fmt.Bytes(1024 * 1024 * 3 / 2), "megabytes with two decimals");
        Check.Equal("14.6 MB", Fmt.Bytes(15_300_000), "megabytes above ten");
        Check.Equal("2.00 GB", Fmt.Bytes(2L * 1024 * 1024 * 1024), "gigabytes");
        Check.Equal("?", Fmt.Bytes(-1), "negative size");
        Check.Contains(Fmt.Speed(1024 * 1024), "MB/sec", "speed formatting");
        Check.Equal("00:30", Fmt.Duration(TimeSpan.FromSeconds(30)), "short duration");
        Check.Equal("01:01:01", Fmt.Duration(TimeSpan.FromSeconds(3661)), "hour-long duration");
        Check.Equal("Unknown", Fmt.Duration(null), "missing duration");
        Check.Equal(50.0, Fmt.Percent(50, 100), "percentage");
        Check.Equal(0.0, Fmt.Percent(50, 0), "percentage without a known total");
        return Task.CompletedTask;
    }

    public static Task FileNameResolverRejectsDangerousNames()
    {
        Check.Equal("evil.exe", FileNameResolver.Sanitize(@"..\..\windows\evil.exe"),
            "path traversal is reduced to a bare file name");
        Check.Equal("passwd", FileNameResolver.Sanitize("/etc/passwd"), "unix paths are stripped");
        Check.Equal("a_b_c.txt", FileNameResolver.Sanitize("a<b>c.txt"), "invalid characters are replaced");
        Check.Equal("_CON.txt", FileNameResolver.Sanitize("CON.txt"), "reserved device names are escaped");
        Check.Equal("download", FileNameResolver.Sanitize("   "), "blank names fall back");
        Check.Equal("download", FileNameResolver.Sanitize(null), "null names fall back to a bare name");
        Check.Equal("file.zip", FileNameResolver.Sanitize("file.zip..."), "trailing dots are trimmed");

        Check.Equal("archive.zip", FileNameResolver.FromUrl("https://host.example/path/archive.zip"),
            "name from a URL path");
        Check.Equal("my report.pdf", FileNameResolver.FromUrl("https://host.example/a/my%20report.pdf"),
            "percent-encoded names are decoded");
        Check.Equal(null, FileNameResolver.FromUrl("https://host.example/"), "a directory URL has no name");

        Check.Equal("download.mp4", FileNameResolver.FromMimeType("video/mp4"), "name from a MIME type");
        Check.Equal("download.bin", FileNameResolver.FromMimeType("application/octet-stream"),
            "binary MIME type");
        Check.Equal("download", FileNameResolver.FromMimeType(null), "missing MIME type");

        var longName = new string('x', 400) + ".txt";
        Check.True(FileNameResolver.Sanitize(longName).Length <= 180, "over-long names are truncated");
        Check.True(FileNameResolver.Sanitize(longName).EndsWith(".txt", StringComparison.Ordinal),
            "truncation keeps the extension");

        return Task.CompletedTask;
    }

    public static Task FileTypeRegistryClassifiesExtensions()
    {
        var registry = new FileTypeRegistry();

        Check.Equal(DownloadCategory.Video, registry.CategoryOf("movie.mkv"), "video extension");
        Check.Equal(DownloadCategory.Music, registry.CategoryOf("song.flac"), "music extension");
        Check.Equal(DownloadCategory.Program, registry.CategoryOf("setup.msi"), "program extension");
        Check.Equal(DownloadCategory.Compressed, registry.CategoryOf("archive.7z"), "archive extension");
        Check.Equal(DownloadCategory.Document, registry.CategoryOf("manual.pdf"), "document extension");
        Check.Equal(DownloadCategory.Other, registry.CategoryOf("mystery"), "unknown extension");

        Check.Equal(".zip", FileTypeRegistry.ExtensionOf("https://host/file.zip?token=abc"), "extension from a URL with a query");
        Check.Equal(".zip", FileTypeRegistry.ExtensionOf("https://host/file.zip#frag"), "extension from a URL with a fragment");
        Check.Equal(".mp4", FileTypeRegistry.ExtensionOf(@"D:\media\clip.mp4"), "extension from a Windows path");
        Check.Equal(string.Empty, FileTypeRegistry.ExtensionOf("https://host/download"), "no extension");

        Check.Equal(FileTypeAction.TakeOver, registry.ActionOf("file.zip"), "archives are taken over by default");
        Check.Equal(FileTypeAction.DoNotTakeOver, registry.ActionOf("page.html"), "web pages are never taken over");
        Check.Equal(FileTypeAction.Ask, registry.ActionOf("unknown.qqq"), "unknown types are asked about");

        Check.True(registry.TakeOverMasks().Contains("*.zip"), "zip is offered to the browser extension");
        Check.True(registry.ExcludedMasks().Contains("*.html"), "html is excluded from takeover");

        Check.Equal(".rar", FileTypeRule.Normalize("*.RAR"), "rule normalization");
        Check.Equal("*.rar", new FileTypeRule { Extension = "rar" }.Mask, "rule mask");

        return Task.CompletedTask;
    }

    public static Task CommandLineParsingWorks()
    {
        var options = CliOptions.Parse(new[]
        {
            "-d", "https://host/a.iso", "-p", @"D:\Downloads", "-f", "image.iso", "-c", "16",
            "--download=https://host/b.zip", "-q", "--description", "two", "-s"
        });

        Check.Equal(0, options.Errors.Count, "no parse errors: " + string.Join("; ", options.Errors));
        Check.Equal(2, options.Requests.Count, "two requests were parsed");

        var first = options.Requests[0];
        Check.Equal("https://host/a.iso", first.Url, "first URL");
        Check.Equal(@"D:\Downloads", first.Directory, "destination folder applies to the following URL");
        Check.Equal("image.iso", first.FileName, "explicit file name");
        Check.Equal(16, first.Connections!.Value, "connection count");

        var second = options.Requests[1];
        Check.Equal("https://host/b.zip", second.Url, "equals-sign form is accepted");
        Check.Equal("two", second.Description, "description");
        Check.True(second.AddToQueue, "-q queues rather than starts");
        Check.False(second.StartNow, "-q implies not starting now");
        Check.True(options.Silent, "-s sets silent mode");

        var slash = CliOptions.Parse(new[] { "/d", "https://host/c.exe", "/p", @"C:\Temp" });
        Check.Equal(1, slash.Requests.Count, "windows-style switches are accepted");
        Check.Equal(@"C:\Temp", slash.Requests[0].Directory, "windows-style folder argument");

        var bare = CliOptions.Parse(new[] { "https://host/d.pdf" });
        Check.Equal(1, bare.Requests.Count, "a bare URL is treated as a download");

        var help = CliOptions.Parse(new[] { "--help" });
        Check.True(help.ShowHelp, "--help");
        Check.Contains(CliOptions.HelpText, "--download", "help text documents --download");

        var bad = CliOptions.Parse(new[] { "--nonsense" });
        Check.Equal(1, bad.Errors.Count, "unknown options are reported");

        var connectionsClamp = CliOptions.Parse(new[] { "-d", "https://h/f", "-c", "999" });
        Check.Equal(32, connectionsClamp.Requests[0].Connections!.Value, "connection counts are clamped");

        return Task.CompletedTask;
    }

    public static Task CredentialProtectionRoundTrips()
    {
        const string secret = "p@ssw0rd with spaces and unicode: \u00e4\u00f6\u00fc\u20ac";

        var protectedValue = CredentialProtector.Protect(secret);
        Check.NotNull(protectedValue, "protected value");
        Check.False(protectedValue!.Contains("p@ssw0rd", StringComparison.Ordinal),
            "the plain text must not survive in the stored form");
        Check.Equal(secret, CredentialProtector.Unprotect(protectedValue), "round trip");

        Check.Equal(null, CredentialProtector.Protect(null), "protecting null yields null");
        Check.Equal(null, CredentialProtector.Unprotect(null), "unprotecting null yields null");
        Check.Equal(null, CredentialProtector.Unprotect(""), "unprotecting empty yields null");

        // Two encryptions of the same value must differ (fresh IV / DPAPI salt).
        var second = CredentialProtector.Protect(secret);
        Check.False(string.Equals(protectedValue, second, StringComparison.Ordinal),
            "each protection uses fresh randomness");
        Check.Equal(secret, CredentialProtector.Unprotect(second), "the second value also round trips");

        return Task.CompletedTask;
    }

    public static Task SettingsJsonRoundTripsFaithfully()
    {
        var settings = AppSettings.CreateDefault();
        settings.Downloads.DefaultDownloadDirectory = @"D:\Test Dir\With Spaces";
        settings.Downloads.PostDownloadAction = PostDownloadAction.OpenFolder;
        settings.Downloads.ExistingFileAction = ExistingFileAction.Rename;
        settings.Connection.MaxConnectionsPerFile = 12;
        settings.Connection.SpeedLimitEnabled = true;
        settings.Connection.SpeedLimitKbPerSecond = 512;
        settings.Connection.ProxyMode = ProxyMode.Manual;
        settings.Scheduler.Enabled = true;
        settings.Scheduler.FinishedActionDelaySeconds = 42;
        settings.Interface.Theme = AppTheme.Dark;
        settings.Interface.WindowLeft = double.NaN;
        settings.General.TakeOverModifier = "Alt";
        settings.BrowserIntegration.MediaOverlay = false;

        var json = JsonSerializer.Serialize(settings, SettingsService.JsonOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions);

        Check.NotNull(restored, "deserialized settings");
        Check.Equal(@"D:\Test Dir\With Spaces", restored!.Downloads.DefaultDownloadDirectory, "path with spaces");
        Check.Equal(PostDownloadAction.OpenFolder, restored.Downloads.PostDownloadAction, "enum stored by name");
        Check.Equal(ExistingFileAction.Rename, restored.Downloads.ExistingFileAction, "second enum");
        Check.Equal(12, restored.Connection.MaxConnectionsPerFile, "connection count");
        Check.Equal(512, restored.Connection.SpeedLimitKbPerSecond, "speed limit");
        Check.Equal(ProxyMode.Manual, restored.Connection.ProxyMode, "proxy mode");
        Check.True(restored.Scheduler.Enabled, "scheduler flag");
        Check.Equal(42, restored.Scheduler.FinishedActionDelaySeconds, "scheduler delay");
        Check.Equal(AppTheme.Dark, restored.Interface.Theme, "theme");
        Check.True(double.IsNaN(restored.Interface.WindowLeft), "NaN survives as a named float literal");
        Check.False(restored.BrowserIntegration.MediaOverlay, "browser integration flag");

        Check.Contains(json, "\"OpenFolder\"", "enums are written as readable names");
        Check.Contains(json, "\"Dark\"", "theme is written as a readable name");

        // TimeSpan handling is custom, so exercise it directly.
        var span = new TimeSpan(23, 30, 15);
        var spanJson = JsonSerializer.Serialize(span, SettingsService.JsonOptions);
        Check.Equal("\"23:30:15\"", spanJson, "TimeSpan is written as a clock string");
        Check.Equal(span, JsonSerializer.Deserialize<TimeSpan>(spanJson, SettingsService.JsonOptions),
            "TimeSpan round trip");

        var withDays = new TimeSpan(2, 3, 4, 5);
        var daysJson = JsonSerializer.Serialize(withDays, SettingsService.JsonOptions);
        Check.Equal(withDays, JsonSerializer.Deserialize<TimeSpan>(daysJson, SettingsService.JsonOptions),
            "multi-day TimeSpan round trip");

        return Task.CompletedTask;
    }

    public static async Task GovernorThrottlesToConfiguredRate()
    {
        var governor = new ThroughputGovernor();
        Check.False(governor.IsEnabled, "throttling is off when the limit is zero");

        governor.LimitBytesPerSecond = 256 * 1024;
        Check.True(governor.IsEnabled, "throttling is on once a limit is set");

        // 512 KB at 256 KB/s: the initial full bucket covers the first 256 KB and
        // the rest must be paced out over roughly one second.
        var stopwatch = Stopwatch.StartNew();
        long transferred = 0;
        while (transferred < 512 * 1024)
        {
            transferred += await governor.AcquireAsync(16 * 1024, CancellationToken.None);
        }
        stopwatch.Stop();

        Check.True(stopwatch.Elapsed.TotalSeconds >= 0.5,
            $"throttling should pace the transfer; it took only {stopwatch.Elapsed.TotalSeconds:0.00}s");
        Check.True(stopwatch.Elapsed.TotalSeconds < 5.0,
            $"throttling should not stall the transfer; it took {stopwatch.Elapsed.TotalSeconds:0.00}s");

        // Disabling the limit must make the next request instant.
        governor.LimitBytesPerSecond = 0;
        var instant = Stopwatch.StartNew();
        var granted = await governor.AcquireAsync(8 * 1024 * 1024, CancellationToken.None);
        instant.Stop();
        Check.Equal(8 * 1024 * 1024, granted, "unlimited mode grants everything requested");
        Check.True(instant.Elapsed.TotalMilliseconds < 200, "unlimited mode does not wait");

        // A cancelled download must not be handed more bytes, even though the bucket
        // still holds a full second of credit. An already-cancelled token makes this
        // deterministic instead of timing dependent.
        governor.LimitBytesPerSecond = 1024;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await governor.AcquireAsync(64 * 1024, cancelled.Token);
            throw new TestFailureException("expected a cancelled acquire to throw");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    public static Task QueueWindowsHandleOvernightRanges()
    {
        var queue = new DownloadQueue
        {
            Id = 1,
            Name = "Night",
            Scheduled = true,
            StartTime = new TimeSpan(23, 0, 0),
            StopTime = new TimeSpan(7, 0, 0),
            Days = 0x7F,
            Enabled = true
        };

        Check.True(queue.RunsOn(DayOfWeek.Monday), "monday is enabled");
        Check.True(queue.StartTime > queue.StopTime, "the window wraps past midnight");
        Check.False(queue.IsDefault, "a named queue is not the default queue");
        Check.True(new DownloadQueue { Id = 0 }.IsDefault, "queue id 0 is the main queue");
        Check.Equal(0x7F, new DownloadQueue().Days, "the default queue runs every day");

        var clone = queue.Clone();
        Check.Equal(queue.Name, clone.Name, "clone copies the name");
        clone.Name = "Changed";
        Check.Equal("Night", queue.Name, "clone is independent");

        return Task.CompletedTask;
    }

    public static Task HostMatcherHandlesSubdomains()
    {
        Check.True(HostMatcher.Matches("example.com", "example.com"), "exact host");
        Check.True(HostMatcher.Matches("example.com", "cdn.example.com"), "sub-domain");
        Check.True(HostMatcher.Matches(".example.com", "cdn.example.com"), "a leading dot is ignored");
        Check.True(HostMatcher.Matches("EXAMPLE.COM", "cdn.example.com"), "matching is case-insensitive");
        Check.True(HostMatcher.Matches("example.com", "EXAMPLE.COM"), "the host is lowercased too");
        Check.False(HostMatcher.Matches("example.com", "notexample.com"), "a suffix is not a sub-domain");
        Check.False(HostMatcher.Matches("example.com", "example.com.evil.net"), "a prefixed domain does not match");
        Check.False(HostMatcher.Matches("", "example.com"), "an empty rule matches nothing");
        Check.False(HostMatcher.Matches("example.com", null), "a missing host matches nothing");

        var list = new List<string> { "a.example.com", ".b.example.com" };
        Check.True(HostMatcher.MatchesAny(list, "x.b.example.com"), "the list matches a sub-domain");
        Check.False(HostMatcher.MatchesAny(list, "c.example.com"), "the list does not over-match");
        Check.False(HostMatcher.MatchesAny(null, "a.example.com"), "a null list matches nothing");
        Check.False(HostMatcher.MatchesAny(list, null), "a null host matches nothing");

        return Task.CompletedTask;
    }

    public static Task ProxyHonoursProtocolSwitchesAndExceptions()
    {
        var settings = new ConnectionSettings
        {
            ProxyAddress = "proxy.example.com",
            ProxyPort = 3128,
            ProxyUsername = "alice",
            UseHttpProxy = true,
            UseHttpsProxy = false,
            UseFtpProxy = true,
            ProxyBypassLocal = false,
            ProxyExceptions = new List<string> { "direct.example.com" }
        };

        var proxy = new OpenDLMProxy(settings, "secret");
        Check.True(proxy.IsUsable, "a proxy with an address is usable");
        Check.NotNull(proxy.Credentials, "proxy credentials are attached");

        Check.NotNull(proxy.GetProxy(new Uri("http://files.example.com/a.zip")),
            "http goes through the proxy");
        Check.Equal(null, proxy.GetProxy(new Uri("https://files.example.com/a.zip")),
            "https is switched off for this proxy");
        Check.NotNull(proxy.GetProxy(new Uri("ftp://files.example.com/a.zip")),
            "ftp still goes through the proxy");
        Check.Equal(null, proxy.GetProxy(new Uri("http://direct.example.com/a.zip")),
            "an exception host connects directly");
        Check.Equal(null, proxy.GetProxy(new Uri("http://cdn.direct.example.com/a.zip")),
            "a sub-domain of an exception connects directly");
        Check.False(proxy.IsBypassed(new Uri("http://files.example.com/a.zip")),
            "IsBypassed agrees that the proxy is used");

        // Plain HTTP proxy: the scheme has to come out as http, not socks.
        Check.Equal("http", proxy.GetProxy(new Uri("http://files.example.com/a.zip"))!.Scheme,
            "the default dialect is HTTP CONNECT");

        var socks = new OpenDLMProxy(
            new ConnectionSettings
            {
                ProxyAddress = "socks.example.com",
                ProxyPort = 1080,
                SocksType = SocksType.Socks5,
                ProxyBypassLocal = false
            },
            null);

        Check.Equal("socks5", socks.GetProxy(new Uri("http://files.example.com/a.zip"))!.Scheme,
            "the SOCKS5 dialect is used");
        Check.Equal(1080, socks.GetProxy(new Uri("http://files.example.com/a.zip"))!.Port,
            "the SOCKS5 port is used");

        var socks4 = new OpenDLMProxy(
            new ConnectionSettings { ProxyAddress = "socks.example.com:9050", SocksType = SocksType.Socks4 },
            null);
        Check.Equal("socks4", socks4.GetProxy(new Uri("http://files.example.com/a.zip"))!.Scheme,
            "the SOCKS4 dialect is used");
        Check.Equal(9050, socks4.GetProxy(new Uri("http://files.example.com/a.zip"))!.Port,
            "a port typed into the address field wins");

        // Local bypass.
        var localBypass = new OpenDLMProxy(
            new ConnectionSettings { ProxyAddress = "proxy.example.com", ProxyBypassLocal = true },
            null);
        Check.Equal(null, localBypass.GetProxy(new Uri("http://localhost/a.zip")), "localhost bypasses the proxy");
        Check.Equal(null, localBypass.GetProxy(new Uri("http://127.0.0.1/a.zip")), "loopback bypasses the proxy");
        Check.Equal(null, localBypass.GetProxy(new Uri("http://192.168.1.10/a.zip")), "a private address bypasses the proxy");
        Check.NotNull(localBypass.GetProxy(new Uri("http://8.8.8.8/a.zip")), "a public address uses the proxy");

        // No address at all: the proxy is unusable and the client should go direct.
        var empty = new OpenDLMProxy(new ConnectionSettings(), null);
        Check.False(empty.IsUsable, "a proxy without an address is not usable");

        return Task.CompletedTask;
    }

    public static Task AdditionalSettingsRoundTrip()
    {
        var settings = AppSettings.CreateDefault();

        settings.Scheduler.DailyLimitEnabled = true;
        settings.Scheduler.DailyLimitHours = 3.5;
        settings.Scheduler.DailyLimitMegabytes = 2500;
        settings.Scheduler.ShowLimitExceededWarning = false;
        settings.Connection.SocksType = SocksType.Socks4;
        settings.Connection.UseHttpsProxy = false;
        settings.Connection.ProxyExceptions = new List<string> { "one.example.com", "two.example.com" };
        settings.Connection.FtpPassive = false;
        settings.Interface.ToolbarStyle = ToolbarStyle.LargeIcons;
        settings.General.EnableForceKey = false;
        settings.General.SkipHtml = false;
        settings.General.RememberLastSave = false;
        settings.Downloads.RememberLastSave = false;
        settings.Sounds.NotifyOnQueueStart = false;
        settings.Sounds.NotifyOnQueueFinish = false;

        var json = JsonSerializer.Serialize(settings, SettingsService.JsonOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions);

        Check.NotNull(restored, "deserialized settings");
        Check.True(restored!.Scheduler.DailyLimitEnabled, "daily limit flag");
        Check.Equal(3.5, restored.Scheduler.DailyLimitHours, "daily limit hours");
        Check.Equal(2500L, restored.Scheduler.DailyLimitMegabytes, "daily limit megabytes");
        Check.False(restored.Scheduler.ShowLimitExceededWarning, "limit warning flag");
        Check.Equal(SocksType.Socks4, restored.Connection.SocksType, "SOCKS dialect");
        Check.False(restored.Connection.UseHttpsProxy, "per-protocol switch");
        Check.Equal(2, restored.Connection.ProxyExceptions.Count, "proxy exception list");
        Check.Equal("two.example.com", restored.Connection.ProxyExceptions[1], "proxy exception value");
        Check.False(restored.Connection.FtpPassive, "FTP passive mode");
        Check.Equal(ToolbarStyle.LargeIcons, restored.Interface.ToolbarStyle, "toolbar style");
        Check.False(restored.General.EnableForceKey, "force key flag");
        Check.False(restored.General.RememberLastSave, "remember last save flag");
        Check.False(restored.Sounds.NotifyOnQueueStart, "queue start notification flag");

        return Task.CompletedTask;
    }
}
