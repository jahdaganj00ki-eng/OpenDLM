using System.Net;
using System.Net.Http.Headers;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Http;

/// <summary>
/// Owns the shared <see cref="HttpClient"/> and rebuilds it when proxy, TLS or
/// user-agent settings change. Connection pooling is important here: a segmented
/// download opens several sockets at once, and re-creating the client per request
/// would exhaust ephemeral ports.
/// </summary>
public sealed class HttpClientProvider : IDisposable
{
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly object _gate = new();
    private HttpClient? _client;
    private string _signature = string.Empty;
    private bool _disposed;

    public HttpClientProvider(Func<AppSettings> settingsAccessor)
    {
        _settingsAccessor = settingsAccessor;
    }

    /// <summary>Returns a client matching the current connection settings, rebuilding it only when needed.</summary>
    public HttpClient Get()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var settings = _settingsAccessor();
            var signature = BuildSignature(settings.Connection);
            if (_client is not null && signature == _signature)
            {
                return _client;
            }

            _client?.Dispose();
            _client = Build(settings.Connection, settings.Advanced);
            _signature = signature;
            return _client;
        }
    }

    private static string BuildSignature(ConnectionSettings connection) => string.Join('|',
        connection.ProxyMode,
        connection.ProxyAddress,
        connection.ProxyPort,
        connection.ProxyUsername,
        connection.ProxyPasswordProtected ?? string.Empty,
        connection.ProxyBypassLocal,
        connection.SocksType,
        connection.UseHttpProxy,
        connection.UseHttpsProxy,
        connection.UseFtpProxy,
        string.Join(',', connection.ProxyExceptions ?? Enumerable.Empty<string>()),
        connection.IgnoreCertificateErrors,
        connection.TimeoutSeconds,
        connection.MaxRedirects,
        connection.UseCustomUserAgent,
        connection.CustomUserAgent);

    private static HttpClient Build(ConnectionSettings connection, AdvancedSettings advanced)
    {
        var handler = new SocketsHttpHandler
        {
            // Redirects are followed manually where we need to inspect each hop,
            // but the default automatic behaviour is fine and faster.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = Math.Clamp(connection.MaxRedirects, 1, 50),
            AutomaticDecompression = DecompressionMethods.All,
            // Cookies are applied per request from the item/extension instead of a
            // shared container, so two downloads from different sessions never mix.
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 5, 120)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = Math.Max(8, connection.MaxConnectionsPerFile * 2)
        };

        switch (connection.ProxyMode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;

            case ProxyMode.Manual:
            {
                // A custom IWebProxy decides per request, which is the only way to
                // honour the per-protocol switches and the host exception list: the
                // handler itself accepts one proxy for everything.
                var proxy = new OpenDLMProxy(
                    connection,
                    CredentialProtectorBridge.Unprotect(connection.ProxyPasswordProtected));

                if (proxy.IsUsable)
                {
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }
                else
                {
                    Log.Warn("The manual proxy has no address; connecting directly.");
                    handler.UseProxy = false;
                }

                break;
            }

            case ProxyMode.Auto:
            case ProxyMode.System:
            default:
                // UseProxy = true with a null Proxy means "use the system settings".
                handler.UseProxy = true;
                break;
        }

        if (connection.IgnoreCertificateErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            // Per-operation timeouts are applied with CancellationToken so a slow but
            // healthy large download is never killed by a global timeout.
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.Clear();
        var userAgent = connection.UseCustomUserAgent && !string.IsNullOrWhiteSpace(connection.CustomUserAgent)
            ? connection.CustomUserAgent
            : DefaultUserAgent;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
        client.DefaultRequestHeaders.ConnectionClose = false;

        Log.Info($"HTTP client built (proxy={connection.ProxyMode}, certCheck={!connection.IgnoreCertificateErrors}).");
        return client;
    }

    /// <summary>User agent used when the user has not supplied one.</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _client?.Dispose();
            _client = null;
        }
    }
}

/// <summary>
/// Indirection so the HTTP layer does not need a direct reference to the settings
/// services namespace, keeping the dependency graph one-directional.
/// </summary>
internal static class CredentialProtectorBridge
{
    public static string? Unprotect(string? value)
        => OpenDLM.Core.Services.CredentialProtector.Unprotect(value);
}
