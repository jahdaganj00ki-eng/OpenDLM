using System.Net;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Http;

/// <summary>
/// The proxy decision, applied per destination rather than globally.
///
/// <see cref="System.Net.Http.SocketsHttpHandler"/> only accepts one proxy for the
/// whole client, but a download manager has to honour three separate things that
/// commercial products expose as settings:
///
///   - a separate on/off switch for http, https and ftp;
///   - a host exception list that goes direct;
///   - a SOCKS dialect instead of an HTTP CONNECT tunnel.
///
/// Implementing <see cref="IWebProxy"/> lets all three be decided for each request.
/// Returning null from <see cref="GetProxy"/> means "connect directly".
/// </summary>
public sealed class OpenDLMProxy : IWebProxy
{
    private readonly Uri? _proxyUri;
    private readonly bool _useHttp;
    private readonly bool _useHttps;
    private readonly bool _useFtp;
    private readonly bool _bypassLocal;
    private readonly List<string> _exceptions;

    /// <summary>Set only in automatic mode, where the script decides per destination.</summary>
    private Func<string, Uri?>? _pac;

    /// <summary>
    /// When the user asks to adopt the browser's proxy, this resolves the system proxy
    /// for a destination. Chrome and Edge on Windows are configured through the
    /// standard WinINET connection settings, so reading them is what "take the
    /// browser's proxy" means here.
    /// </summary>
    private Func<Uri, Uri?>? _fromBrowser;

    public OpenDLMProxy(ConnectionSettings settings, string? plainProxyPassword)
    {
        _useHttp = settings.UseHttpProxy;
        _useHttps = settings.UseHttpsProxy;
        _useFtp = settings.UseFtpProxy;
        _bypassLocal = settings.ProxyBypassLocal;
        _exceptions = settings.ProxyExceptions ?? new List<string>();

        _takeHttpFromBrowser = settings.TakeHttpProxyFromBrowser;
        _takeHttpsFromBrowser = settings.TakeHttpsProxyFromBrowser;
        _takeFtpFromBrowser = settings.TakeFtpProxyFromBrowser;

        if (_takeHttpFromBrowser || _takeHttpsFromBrowser || _takeFtpFromBrowser)
        {
            _fromBrowser = ResolveSystemProxy;
        }

        _proxyUri = BuildProxyUri(settings);

        if (_proxyUri is not null && !string.IsNullOrEmpty(settings.ProxyUsername))
        {
            Credentials = new NetworkCredential(settings.ProxyUsername, plainProxyPassword ?? string.Empty);
        }
    }

    /// <summary>
    /// Builds the proxy used by the automatic mode, which consults a PAC script for
    /// every destination instead of routing everything through one address.
    /// Returns null when no script is configured, so the caller can fall back to the
    /// system settings.
    /// </summary>
    public static OpenDLMProxy? ForAutomaticMode(ConnectionSettings settings, PacResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(settings.ProxyPacUrl) || !resolver.IsAvailable)
        {
            return null;
        }

        var pacUrl = settings.ProxyPacUrl.Trim();
        return new OpenDLMProxy(settings, null).WithPac(url => resolver.Resolve(url, pacUrl));
    }

    private OpenDLMProxy WithPac(Func<string, Uri?> evaluator)
    {
        _pac = evaluator;
        return this;
    }

    /// <summary>
    /// Reads the proxy Windows is configured with for a destination. Chrome and Edge
    /// both use the WinINET connection settings, so this is what "the browser's proxy"
    /// means on Windows; a direct result is reported as null.
    /// </summary>
    private static Uri? ResolveSystemProxy(Uri destination)
    {
        try
        {
            // The default proxy honours the WinINET settings and any auto-config URL,
            // which is the same source the browsers read.
            var proxy = System.Net.WebRequest.GetSystemWebProxy()
                .GetProxy(destination);

            if (proxy is null)
            {
                return null;
            }

            // A proxy that answers with the destination itself means "no proxy".
            return proxy.Host.Equals(destination.Host, StringComparison.OrdinalIgnoreCase)
                ? null
                : proxy;
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("Could not read the system proxy: " + ex.Message);
            return null;
        }
    }

    private bool BrowserForbids(Uri destination)
    {
        if (_fromBrowser is null)
        {
            return true;
        }

        if (destination.Scheme == Uri.UriSchemeHttp)
        {
            return !_takeHttpFromBrowser;
        }

        if (destination.Scheme == Uri.UriSchemeHttps)
        {
            return !_takeHttpsFromBrowser;
        }

        if (destination.Scheme == Uri.UriSchemeFtp)
        {
            return !_takeFtpFromBrowser;
        }

        return true;
    }

    private bool _takeHttpFromBrowser;
    private bool _takeHttpsFromBrowser;
    private bool _takeFtpFromBrowser;

    public ICredentials? Credentials { get; set; }

    /// <summary>True when this proxy has somewhere to send requests.</summary>
    public bool IsUsable => _pac is not null || _proxyUri is not null;

    /// <summary>The configured proxy, or null to connect directly.</summary>
    public Uri? GetProxy(Uri destination)
    {
        if (destination is null)
        {
            return null;
        }

        // A user exception always wins, even over what the script says.
        if (IsExcluded(destination))
        {
            return null;
        }

        if (_pac is not null)
        {
            // The script decides; DIRECT and failures both come back as null.
            return _pac(destination.AbsoluteUri);
        }

        if (IsProtocolDisabled(destination))
        {
            return null;
        }

        // A scheme set to "use the browser's proxy" prefers whatever Windows is
        // configured with, and only falls back to the manual address if that yields
        // nothing.
        if (_fromBrowser is not null && !BrowserForbids(destination))
        {
            var browserProxy = _fromBrowser(destination);
            if (browserProxy is not null)
            {
                return browserProxy;
            }
        }

        return _proxyUri;
    }

    public bool IsBypassed(Uri host) => GetProxy(host) is null;

    private bool IsProtocolDisabled(Uri destination)
    {
        if (destination.Scheme == Uri.UriSchemeHttp && !_useHttp)
        {
            return true;
        }

        if (destination.Scheme == Uri.UriSchemeHttps && !_useHttps)
        {
            return true;
        }

        if (destination.Scheme == Uri.UriSchemeFtp && !_useFtp)
        {
            return true;
        }

        return false;
    }

    private bool IsExcluded(Uri destination)
    {
        if (_bypassLocal && IsLocal(destination))
        {
            return true;
        }

        return HostMatcher.MatchesAny(_exceptions, destination.Host);
    }

    /// <summary>Loopback, link-local and single-label hosts are treated as local.</summary>
    private static bool IsLocal(Uri destination)
    {
        var host = destination.Host;

        return IPAddress.TryParse(host, out var address)
            ? IPAddress.IsLoopback(address) || IsPrivate(address)
            : !host.Contains('.', StringComparison.Ordinal) ||
              host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254);
    }

    /// <summary>
    /// Builds the proxy URI. A SOCKS dialect wins over the plain HTTP proxy, because
    /// choosing SOCKS in the options is an explicit statement about how the tunnel
    /// should be established.
    /// </summary>
    private static Uri? BuildProxyUri(ConnectionSettings settings)
    {
        var address = (settings.ProxyAddress ?? string.Empty).Trim();
        if (address.Length == 0)
        {
            return null;
        }

        // Strip any scheme the user typed; the dialect decides it.
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            address = address[(schemeEnd + 3)..];
        }

        address = address.TrimEnd('/');
        if (address.Length == 0)
        {
            return null;
        }

        // A port typed into the address field wins over the numeric field.
        if (!address.Contains(':', StringComparison.Ordinal))
        {
            address = $"{address}:{Math.Clamp(settings.ProxyPort, 1, 65535)}";
        }

        var scheme = settings.SocksType switch
        {
            SocksType.Socks4 => "socks4",
            SocksType.Socks5 => "socks5",
            _ => "http"
        };

        return Uri.TryCreate($"{scheme}://{address}", UriKind.Absolute, out var uri) ? uri : null;
    }
}
