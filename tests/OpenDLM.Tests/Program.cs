using System.Diagnostics;
using OpenDLM.Core.Util;
using OpenDLM.Tests;

// This must run before any OpenDLM type touches AppPaths: OpenDLM honours the
// OPEN_DLM_HOME environment variable as a portable root, and pointing it at a
// throwaway directory keeps the suite from writing into the real user profile.
var sandbox = Path.Combine(Path.GetTempPath(), "opendlm-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(sandbox);
Environment.SetEnvironmentVariable("OPEN_DLM_HOME", sandbox);

Console.WriteLine("OpenDLM test suite");
Console.WriteLine("Sandbox: " + sandbox);
Console.WriteLine("Framework: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
Console.WriteLine(new string('-', 78));

var cases = new List<(string Name, Func<Task> Body)>
{
    // Pure logic: no network, no disk.
    ("Segment planner covers the file exactly", UnitTests.SegmentPlannerCoversFileExactly),
    ("Segment planner handles an unknown size", UnitTests.SegmentPlannerHandlesUnknownSize),
    ("Byte formatting is readable", UnitTests.ByteFormattingIsReadable),
    ("File name resolver rejects dangerous names", UnitTests.FileNameResolverRejectsDangerousNames),
    ("File type registry classifies extensions", UnitTests.FileTypeRegistryClassifiesExtensions),
    ("Command line parsing works", UnitTests.CommandLineParsingWorks),
    ("Credential protection round trips", UnitTests.CredentialProtectionRoundTrips),
    ("Settings JSON round trips faithfully", UnitTests.SettingsJsonRoundTripsFaithfully),
    ("Governor throttles to the configured rate", UnitTests.GovernorThrottlesToConfiguredRate),
    ("Queue windows handle overnight ranges", UnitTests.QueueWindowsHandleOvernightRanges),
    ("Host matcher handles sub-domains", UnitTests.HostMatcherHandlesSubdomains),
    ("Proxy honours protocol switches and exceptions", UnitTests.ProxyHonoursProtocolSwitchesAndExceptions),
    ("Added settings round trip", UnitTests.AdditionalSettingsRoundTrip),

    // End to end against a real local HTTP server.
    ("Multi-connection download is byte perfect", () => EngineTests.MultiConnectionDownloadIsBytePerfect(sandbox)),
    ("Probe reports size and range support", () => EngineTests.ProbeReportsSizeAndRangeSupport(sandbox)),
    ("Server without range support falls back to one connection", () => EngineTests.ServerWithoutRangeSupportFallsBackToSingleConnection(sandbox)),
    ("Dropped connections are resumed, not restarted", () => EngineTests.DroppedConnectionsAreResumedNotRestarted(sandbox)),
    ("Pause and resume keeps data", () => EngineTests.PauseAndResumeKeepsData(sandbox)),
    ("Stop leaves the item restartable", () => EngineTests.StopLeavesItemRestartable(sandbox)),
    ("Missing file reports not found", () => EngineTests.MissingFileReportsNotFound(sandbox)),
    ("Authentication challenge is answered from the UI", () => EngineTests.AuthenticationChallengeIsAnsweredFromTheUi(sandbox)),
    ("Existing file is renamed rather than overwritten", () => EngineTests.ExistingFileIsRenamedRatherThanOverwritten(sandbox)),
    ("Category folders are honoured", () => EngineTests.CategoryFoldersAreHonoured(sandbox)),
    ("Checksums are computed when enabled", () => EngineTests.ChecksumsAreComputedWhenEnabled(sandbox)),
    ("Speed limit slows the transfer", () => EngineTests.SpeedLimitSlowsTheTransfer(sandbox)),
    ("Queued downloads wait for start", () => EngineTests.QueuedDownloadsWaitForStart(sandbox)),
    ("Site exception forces a single connection", () => EngineTests.SiteExceptionForcesASingleConnection(sandbox)),
    ("Per-type folder overrides the category folder", () => EngineTests.PerTypeFolderOverridesTheCategoryFolder(sandbox)),
    ("Item carries the queue name", () => EngineTests.ItemCarriesTheQueueName(sandbox)),
    ("Site exceptions persist", () => EngineTests.SiteExceptionsPersist(sandbox)),

    // FTP, with a real passive-mode FTP server on a raw socket.
    ("FTP probe reports size", () => EngineTests.FtpProbeReportsSize(sandbox)),
    ("FTP download is byte perfect", () => EngineTests.FtpDownloadIsBytePerfect(sandbox)),
    ("FTP resumes after dropped connections", () => EngineTests.FtpResumesAfterDroppedConnections(sandbox)),
    ("FTP rejects an ignored restart marker", () => EngineTests.FtpRejectsAnIgnoredRestartMarker(sandbox)),
    ("FTP authentication is answered from the UI", () => EngineTests.FtpAuthenticationIsAnsweredFromTheUi(sandbox))
};

var failures = new List<string>();
var suiteWatch = Stopwatch.StartNew();

foreach (var (name, body) in cases)
{
    var caseWatch = Stopwatch.StartNew();
    try
    {
        await body();
        caseWatch.Stop();
        Console.WriteLine($"  PASS  {name}  ({caseWatch.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex)
    {
        caseWatch.Stop();
        failures.Add(name);
        Console.WriteLine($"  FAIL  {name}  ({caseWatch.ElapsedMilliseconds} ms)");
        Console.WriteLine("        " + ex.GetType().Name + ": " + ex.Message);
    }
}

suiteWatch.Stop();

Console.WriteLine(new string('-', 78));
Console.WriteLine($"{(cases.Count - failures.Count)} passed, {failures.Count} failed " +
                  $"in {suiteWatch.Elapsed.TotalSeconds:0.0}s");

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failed tests:");
    foreach (var failure in failures)
    {
        Console.WriteLine("  - " + failure);
    }
    Console.WriteLine();
    Console.WriteLine("Log: " + AppPaths.LogFile);
}

// Best effort cleanup; partial files may still be held briefly by the OS.
try
{
    Directory.Delete(sandbox, recursive: true);
}
catch
{
    // Leaving the sandbox behind is harmless.
}

return failures.Count == 0 ? 0 : 1;
