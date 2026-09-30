using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Http;

/// <summary>
/// Everything one HTTP operation needs: the client, a settings snapshot and the
/// identity-related bits (referer, cookies, credentials) that must be replayed on
/// every request of a segmented download.
/// </summary>
public sealed class DownloadContext
{
    public required HttpClient Client { get; init; }
    public required AppSettings Settings { get; init; }
    public required ThroughputGovernor Governor { get; init; }
    public IReadOnlyList<SiteLogin> SiteLogins { get; init; } = Array.Empty<SiteLogin>();
    public Action<string>? LogMessage { get; init; }

    /// <summary>The user agent actually sent, so the resume state can detect a change.</summary>
    public string EffectiveUserAgent =>
        Settings.Connection.UseCustomUserAgent && !string.IsNullOrWhiteSpace(Settings.Connection.CustomUserAgent)
            ? Settings.Connection.CustomUserAgent
            : HttpClientProvider.DefaultUserAgent;

    public void Log(string message) => LogMessage?.Invoke(message);
}

/// <summary>Builds the <see cref="HttpRequestMessage"/>s used by the probe and the segment workers.</summary>
public static class HttpRequestFactory
{
    /// <summary>
    /// Creates a request with the identity headers already applied.
    /// <paramref name="rangeFrom"/> / <paramref name="rangeTo"/> add a byte range when supplied.
    /// </summary>
    public static HttpRequestMessage Create(
        HttpMethod method,
        string url,
        DownloadContext context,
        DownloadItem? item = null,
        long? rangeFrom = null,
        long? rangeTo = null,
        bool addRange = true)
    {
        var request = new HttpRequestMessage(method, url);

        if (addRange && rangeFrom.HasValue)
        {
            var value = rangeTo.HasValue
                ? $"bytes={rangeFrom.Value}-{rangeTo.Value}"
                : $"bytes={rangeFrom.Value}-";
            request.Headers.Range = RangeHeaderValue.Parse(value);
        }

        var settings = context.Settings;

        if (settings.Advanced.SendReferer)
        {
            var referer = FirstNonEmpty(item?.Referer, item?.PageUrl);
            if (!string.IsNullOrWhiteSpace(referer) &&
                Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                request.Headers.Referrer = refererUri;
            }
        }

        if (settings.Advanced.SendCookies && !string.IsNullOrWhiteSpace(item?.Cookies))
        {
            // TryAddWithoutValidation: cookie strings from a browser are not always
            // RFC-compliant and must be forwarded verbatim.
            request.Headers.TryAddWithoutValidation("Cookie", item!.Cookies);
        }

        var userAgent = !string.IsNullOrWhiteSpace(item?.UserAgent)
            ? item!.UserAgent
            : context.EffectiveUserAgent;
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        ApplyCredentials(request, url, context, item);

        return request;
    }

    private static void ApplyCredentials(HttpRequestMessage request, string url, DownloadContext context, DownloadItem? item)
    {
        // 1. Credentials attached to the specific download win.
        if (!string.IsNullOrWhiteSpace(item?.Username))
        {
            request.Headers.Authorization = BasicAuth(item!.Username, item.Password);
            return;
        }

        // 2. Otherwise fall back to the stored site-login list.
        if (context.SiteLogins.Count == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        foreach (var login in context.SiteLogins)
        {
            if (login.Matches(uri))
            {
                var password = login.PlainPassword ?? Services.CredentialProtector.Unprotect(login.PasswordProtected);
                request.Headers.Authorization = BasicAuth(login.Username, password);
                context.Log($"Using stored login for {uri.Host} as '{login.Username}'.");
                return;
            }
        }
    }

    /// <summary>Splits a value the server may have sent as either Basic or Digest into a Basic header.</summary>
    public static AuthenticationHeaderValue BasicAuth(string username, string? password)
    {
        var raw = Encoding.UTF8.GetBytes($"{username}:{password ?? string.Empty}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }

    /// <summary>Applies the stored site login to a request after a 401 told us which realm to use.</summary>
    public static bool TryApplyRealmCredentials(HttpRequestMessage request, Uri uri, IReadOnlyList<SiteLogin> logins)
    {
        foreach (var login in logins)
        {
            if (!login.Matches(uri))
            {
                continue;
            }
            var password = login.PlainPassword ?? Services.CredentialProtector.Unprotect(login.PasswordProtected);
            request.Headers.Authorization = BasicAuth(login.Username, password);
            return true;
        }
        return false;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>Maps transport and HTTP failures onto the user-facing <see cref="DownloadErrorKind"/>.</summary>
public static class ErrorMapper
{
    /// <summary>True for the FTP reply codes that mean "give me credentials".</summary>
    private static bool IsFtpAuthenticationFailure(WebException exception)
    {
        if (exception.Response is FtpWebResponse response)
        {
            return (int)response.StatusCode is 530 or 531 or 532;
        }

        // No response body to inspect, so fall back to the wording .NET uses.
        var message = exception.Message;
        return message.Contains("Not logged on", StringComparison.OrdinalIgnoreCase)
               || message.Contains("530", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when trying the exact same request again could plausibly succeed.
    /// A 404 or a full disk will not fix itself, and retrying those only delays
    /// the error the user needs to see.
    /// </summary>
    public static bool IsRetryable(DownloadErrorKind kind) => kind switch
    {
        DownloadErrorKind.NotFound => false,
        DownloadErrorKind.Forbidden => false,
        DownloadErrorKind.AuthenticationRequired => false,
        DownloadErrorKind.InvalidUrl => false,
        DownloadErrorKind.DiskFull => false,
        DownloadErrorKind.DiskError => false,
        DownloadErrorKind.FileInUse => false,
        DownloadErrorKind.Cancelled => false,
        DownloadErrorKind.SizeMismatch => false,
        DownloadErrorKind.None => false,
        _ => true
    };

    public static (DownloadErrorKind Kind, string Message) Map(Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return (DownloadErrorKind.Cancelled, "The download was cancelled.");

            case HttpRequestException http:
                return MapHttp(http);

            // FTP reports a failed login as a WebException wrapping the response,
            // not as an HttpRequestException. Without this case a 530 would be
            // classified as a generic (and retryable) failure, so the user would
            // never be asked for the user name and password the server wants.
            case WebException web when IsFtpAuthenticationFailure(web):
                return (DownloadErrorKind.AuthenticationRequired,
                    "The server requires a user name and password.");

            case WebException web:
                return (DownloadErrorKind.Unknown, web.Message);

            case UnauthorizedAccessException:
                return (DownloadErrorKind.DiskError, "Access to the destination file was denied.");

            // A body that ends early surfaces as a plain IOException (HttpIOException in
            // .NET 8), NOT as an HttpRequestException. Classifying it as a disk error
            // would make a recoverable interruption permanent, so the truncation
            // wording is tested before the generic IOException case.
            case IOException io when IsTruncatedResponse(io):
                return (DownloadErrorKind.ConnectionReset,
                    "The connection closed before the response was complete: " + io.Message);

            case IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase):
                return (DownloadErrorKind.DiskFull, "Not enough free space on the destination drive.");

            case IOException io:
                return (DownloadErrorKind.DiskError, "A disk error occurred: " + io.Message);

            case UriFormatException:
                return (DownloadErrorKind.InvalidUrl, "The URL is not valid.");

            case TimeoutException:
                return (DownloadErrorKind.Timeout, "The server did not answer in time.");

            default:
                return (DownloadErrorKind.Unknown, exception.Message);
        }
    }

    /// <summary>
    /// Recognises the several wordings the BCL uses when an HTTP response body ends
    /// before its declared length. .NET 8 reports this as <c>HttpIOException</c> with
    /// "The response ended prematurely...". It is a transport failure, not a disk
    /// failure, and retrying it is exactly the right thing to do.
    /// </summary>
    private static bool IsTruncatedResponse(IOException exception)
    {
        var message = exception.Message;
        return message.Contains("prematurely", StringComparison.OrdinalIgnoreCase)
               || message.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase)
               || message.Contains("unexpected end", StringComparison.OrdinalIgnoreCase)
               || message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase);
    }

    private static (DownloadErrorKind, string) MapHttp(HttpRequestException exception)
    {
        var message = exception.Message;

        if (exception.StatusCode is { } status)
        {
            var code = (int)status;
            return code switch
            {
                401 => (DownloadErrorKind.AuthenticationRequired, "The server requires a user name and password."),
                403 => (DownloadErrorKind.Forbidden, "The server refused the request (403 Forbidden)."),
                404 => (DownloadErrorKind.NotFound, "The file was not found on the server (404)."),
                407 => (DownloadErrorKind.AuthenticationRequired, "The proxy requires a user name and password."),
                416 => (DownloadErrorKind.RangeNotSupported, "The server rejected the requested byte range."),
                429 => (DownloadErrorKind.HttpError, "The server is rate limiting requests (429). Try again later."),
                >= 500 => (DownloadErrorKind.HttpError, $"The server reported an error ({code})."),
                _ => (DownloadErrorKind.HttpError, $"The server reported HTTP {code} {status}.")
            };
        }

        if (message.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase))
        {
            return (DownloadErrorKind.Dns, "The server name could not be resolved.");
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return (DownloadErrorKind.Timeout, "The connection timed out.");
        }

        if (message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("prematurely", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("end of the response", StringComparison.OrdinalIgnoreCase))
        {
            return (DownloadErrorKind.ConnectionReset, "The connection was closed unexpectedly by the server.");
        }

        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return (DownloadErrorKind.HttpError, "A secure connection could not be established: " + message);
        }

        return (DownloadErrorKind.Unknown, message);
    }
}
