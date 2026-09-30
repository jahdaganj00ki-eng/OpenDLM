using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Http;

/// <summary>
/// Evaluates a PAC (proxy auto-configuration) script per destination.
///
/// The BCL has no PAC evaluator, but Windows does: <c>WinHttpGetProxyForUrl</c>
/// runs the script itself, so there is no JavaScript engine to embed and no PAC
/// dialect to reimplement. That is a much smaller and far more compatible surface
/// than writing an evaluator, and it is exactly what the operating system already
/// uses for its own WinHTTP traffic.
///
/// Results are cached per destination host, because a PAC is consulted on every
/// connection setup and fetching the script each time would be absurdly expensive.
/// </summary>
public sealed class PacResolver : IDisposable
{
    private const uint AutoProxyAutoDetect = 0x00000001;
    private const uint AutoProxyConfigUrl = 0x00000002;

    private readonly IntPtr _session;
    private readonly ConcurrentDictionary<string, Uri?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PacResolver()
    {
        try
        {
            // User agent, no explicit proxy, no automatic proxy access for the
            // session itself (it is only used to evaluate a script).
            _session = WinHttpOpen("OpenDLM/1.0", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open a WinHTTP session for PAC evaluation: " + ex.Message);
            _session = IntPtr.Zero;
        }
    }

    /// <summary>False when Windows refused the session, in which case everything goes direct.</summary>
    public bool IsAvailable => _session != IntPtr.Zero;

    /// <summary>Number of distinct hosts resolved so far, for diagnostics.</summary>
    public int CachedHosts => _cache.Count;

    /// <summary>
    /// Returns the proxy to use for <paramref name="targetUrl"/>, or null when the
    /// script says DIRECT, when it cannot be evaluated, or when there is no script.
    /// </summary>
    public Uri? Resolve(string targetUrl, string? pacUrl)
    {
        if (!IsAvailable || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var target))
        {
            return null;
        }

        var key = string.Concat(target.Scheme, "://", target.Host, ":", target.Port, "|", pacUrl ?? string.Empty);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = Evaluate(target.AbsoluteUri, pacUrl);
        _cache[key] = result;
        return result;
    }

    /// <summary>Drops the cache, for when the script or the settings change.</summary>
    public void ClearCache() => _cache.Clear();

    private Uri? Evaluate(string targetUrl, string? pacUrl)
    {
        var optionsPtr = IntPtr.Zero;
        var urlPtr = IntPtr.Zero;
        IntPtr proxyInfo = IntPtr.Zero;

        try
        {
            // dwFlags: the script URL is given, and auto-detect is tried as well so
            // a DHCP or WPAD answer still works alongside an explicit script.
            var flags = AutoProxyAutoDetect;
            var script = (pacUrl ?? string.Empty).Trim();

            if (script.Length > 0)
            {
                flags |= AutoProxyConfigUrl;
                urlPtr = Marshal.StringToHGlobalUni(script);
            }

            optionsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinHttpAutoProxyOptions>());
            Marshal.StructureToPtr(
                new WinHttpAutoProxyOptions
                {
                    Flags = flags,
                    AutoConfigUrl = urlPtr,
                    Reserved = IntPtr.Zero,
                    ReservedValue = 0
                },
                optionsPtr,
                fDeleteOld: false);

            if (!WinHttpGetProxyForUrl(_session, targetUrl, optionsPtr, out proxyInfo))
            {
                Log.Warn($"PAC evaluation failed for {targetUrl} (Win32 {Marshal.GetLastWin32Error()}).");
                return null;
            }

            var text = Marshal.PtrToStringUni(proxyInfo);
            return ParseProxyString(text);
        }
        catch (Exception ex)
        {
            Log.Warn("PAC evaluation failed for " + targetUrl + ": " + ex.Message);
            return null;
        }
        finally
        {
            if (proxyInfo != IntPtr.Zero)
            {
                // WinHTTP allocates this with GlobalAlloc.
                Marshal.FreeHGlobal(proxyInfo);
            }
            if (optionsPtr != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinHttpAutoProxyOptions>(optionsPtr);
                Marshal.FreeHGlobal(optionsPtr);
            }
            if (urlPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(urlPtr);
            }
        }
    }

    /// <summary>
    /// Turns a PAC result such as "PROXY host:3128; SOCKS host:1080" into a URI.
    /// DIRECT, and anything unparseable, yields null so the request goes direct.
    /// </summary>
    public static Uri? ParseProxyString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var directive in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = directive.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            var space = part.IndexOf(' ');
            if (space < 0)
            {
                // A bare "DIRECT" means go direct.
                continue;
            }

            var kind = part[..space].Trim();
            var endpoint = part[(space + 1)..].Trim();

            if (!kind.Equals("PROXY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (endpoint.Length == 0)
            {
                continue;
            }

            var scheme = "http://";

            if (!endpoint.Contains(':'))
            {
                endpoint += ":8080";
            }

            if (Uri.TryCreate(scheme + endpoint, UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_session != IntPtr.Zero)
        {
            try
            {
                WinHttpCloseHandle(_session);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not close the WinHTTP session: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct WinHttpAutoProxyOptions
    {
        public uint Flags;
        public IntPtr AutoConfigUrl;
        public IntPtr Reserved;
        public uint ReservedValue;
    }

    [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WinHttpOpen")]
    private static extern IntPtr WinHttpOpen(
        string? agent, IntPtr proxyBypass, IntPtr proxyAccess, IntPtr bypassList, uint flags);

    [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WinHttpGetProxyForUrl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetProxyForUrl(
        IntPtr session, string url, IntPtr autoProxyOptions, out IntPtr proxyInfo);

    [DllImport("winhttp.dll", SetLastError = true, EntryPoint = "WinHttpCloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpCloseHandle(IntPtr handle);
}
