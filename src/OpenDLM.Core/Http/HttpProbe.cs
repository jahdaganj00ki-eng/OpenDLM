using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Http;

/// <summary>
/// Determines a URL's size, whether it accepts byte ranges, and what the file
/// should be called, before any real downloading happens.
///
/// The primary probe is a single <c>GET</c> with <c>Range: bytes=0-0</c>, because
/// that one request answers all three questions at once:
///   - <c>206 Partial Content</c> + <c>Content-Range: bytes 0-0/12345</c> = size known and ranges supported
///   - <c>200 OK</c> + <c>Content-Length</c> = size known, ranges not supported
///   - no length at all = unknown size, single-stream download
/// A <c>HEAD</c> request is only used as a fallback for servers that reject ranged GETs.
/// </summary>
public static class HttpProbe
{
    public static async Task<ProbeResult> ProbeAsync(
        string url,
        DownloadContext context,
        DownloadItem? item,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ProbeResult.Failed("Only http:// and https:// URLs are supported.", DownloadErrorKind.InvalidUrl);
        }

        // Step 1: ranged GET.
        var ranged = await TryRangedGetAsync(url, context, item, cancellationToken).ConfigureAwait(false);
        if (ranged is not null)
        {
            return ranged;
        }

        // Step 2: HEAD fallback.
        if (context.Settings.Connection.UseHeadProbe)
        {
            var head = await TryHeadAsync(url, context, item, cancellationToken).ConfigureAwait(false);
            if (head is not null)
            {
                return head;
            }
        }

        // Step 3: plain GET, headers only.
        return await TryPlainGetAsync(url, context, item, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProbeResult?> TryRangedGetAsync(
        string url, DownloadContext context, DownloadItem? item, CancellationToken cancellationToken)
    {
        try
        {
            using var request = HttpRequestFactory.Create(HttpMethod.Get, url, context, item, rangeFrom: 0, rangeTo: 0);
            using var response = await SendAsync(context, request, cancellationToken).ConfigureAwait(false);

            if (IsRetryableProbeFailure(response.StatusCode))
            {
                return null; // let the caller try HEAD / plain GET
            }

            return Describe(response, url, rangedProbe: true, allowRange: response.StatusCode == HttpStatusCode.PartialContent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log($"Ranged GET probe failed for {url}: {ex.Message}");
            return null;
        }
    }

    private static async Task<ProbeResult?> TryHeadAsync(
        string url, DownloadContext context, DownloadItem? item, CancellationToken cancellationToken)
    {
        try
        {
            using var request = HttpRequestFactory.Create(HttpMethod.Head, url, context, item, addRange: false);
            using var response = await SendAsync(context, request, cancellationToken).ConfigureAwait(false);

            if (IsRetryableProbeFailure(response.StatusCode))
            {
                return null;
            }

            var result = Describe(response, url, rangedProbe: false, allowRange: false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log($"HEAD probe failed for {url}: {ex.Message}");
            return null;
        }
    }

    private static async Task<ProbeResult> TryPlainGetAsync(
        string url, DownloadContext context, DownloadItem? item, CancellationToken cancellationToken)
    {
        try
        {
            using var request = HttpRequestFactory.Create(HttpMethod.Get, url, context, item, addRange: false);
            using var response = await SendAsync(context, request, cancellationToken).ConfigureAwait(false);
            return Describe(response, url, rangedProbe: false, allowRange: false);
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.Failed("The probe was cancelled.", DownloadErrorKind.Cancelled);
        }
        catch (Exception ex)
        {
            var (kind, message) = ErrorMapper.Map(ex);
            return ProbeResult.Failed(message, kind);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        DownloadContext context, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(context.Settings.Connection.TimeoutSeconds, 5, 3600));

        // The linked source must outlive the send, so it is scoped to this method
        // rather than returned as a bare token (which would leak the registration).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        return await context.Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
    }

    private static bool IsRetryableProbeFailure(HttpStatusCode status)
        => status is HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented
            or HttpStatusCode.Forbidden
            or HttpStatusCode.BadRequest;

    private static ProbeResult Describe(HttpResponseMessage response, string originalUrl, bool rangedProbe, bool allowRange)
    {
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
        var content = response.Content;
        var headers = content.Headers;

        long total = -1;
        var supportsRanges = false;

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            supportsRanges = true;
            total = TotalFromContentRange(headers.ContentRange);
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            total = headers.ContentLength ?? -1;
            // A server that answers a ranged request with 200 does not support ranges.
            // Accept-Ranges is a response header, not a content header.
            supportsRanges = !rangedProbe &&
                             response.Headers.AcceptRanges.Any(v => v.Equals("bytes", StringComparison.OrdinalIgnoreCase));
        }

        var disposition = headers.ContentDisposition;
        var fileName = FileNameResolver.FromContentDisposition(disposition);
        fileName ??= FileNameResolver.FromUrl(finalUrl);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = FileNameResolver.FromMimeType(headers.ContentType?.MediaType);
        }
        fileName = FileNameResolver.Sanitize(fileName!);

        return new ProbeResult
        {
            Success = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent,
            ContentLength = total,
            SupportsRanges = supportsRanges,
            SuggestedFileName = fileName,
            MimeType = headers.ContentType?.MediaType,
            FinalUrl = finalUrl,
            StatusCode = (int)response.StatusCode,
            ContentDisposition = disposition?.ToString(),
            ETag = response.Headers.ETag?.Tag,
            LastModified = headers.LastModified?.ToString("R", CultureInfo.InvariantCulture),
            ErrorMessage = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent
                ? null
                : $"The server returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
            ErrorKind = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent
                ? DownloadErrorKind.None
                : KindFromStatus(response.StatusCode)
        };
    }

    private static DownloadErrorKind KindFromStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => DownloadErrorKind.AuthenticationRequired,
        HttpStatusCode.ProxyAuthenticationRequired => DownloadErrorKind.AuthenticationRequired,
        HttpStatusCode.Forbidden => DownloadErrorKind.Forbidden,
        HttpStatusCode.NotFound => DownloadErrorKind.NotFound,
        HttpStatusCode.RequestedRangeNotSatisfiable => DownloadErrorKind.RangeNotSupported,
        _ => DownloadErrorKind.HttpError
    };

    /// <summary>Reads the total size out of a <c>Content-Range: bytes 0-0/12345</c> header.</summary>
    private static long TotalFromContentRange(ContentRangeHeaderValue? range)
    {
        if (range is null)
        {
            return -1;
        }
        if (range.Length.HasValue)
        {
            return range.Length.Value;
        }
        // "bytes 0-0/*" means the server does not know the total up front.
        return -1;
    }
}

/// <summary>Turns server-supplied strings into a file name that is safe on Windows.</summary>
public static class FileNameResolver
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private const int MaxLength = 180;

    /// <summary>Extracts a name from <c>Content-Disposition</c>, honouring the RFC 5987 <c>filename*</c> form.</summary>
    public static string? FromContentDisposition(ContentDispositionHeaderValue? disposition)
    {
        if (disposition is null)
        {
            return null;
        }

        var star = disposition.FileNameStar;
        if (!string.IsNullOrWhiteSpace(star))
        {
            return Decode(star);
        }

        var plain = disposition.FileName;
        if (!string.IsNullOrWhiteSpace(plain))
        {
            return Decode(plain);
        }

        return null;
    }

    private static string Decode(string value)
    {
        var trimmed = value.Trim().Trim('"');
        try
        {
            // The header may arrive percent-encoded (RFC 5987) or, incorrectly,
            // as a raw latin-1 string. Prefer the decoded form when it round-trips.
            var decoded = Uri.UnescapeDataString(trimmed);
            return decoded;
        }
        catch
        {
            return trimmed;
        }
    }

    /// <summary>Derives a name from the last path segment of a URL.</summary>
    public static string? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        var segment = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        if (segment.Length == 0)
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(segment);
        return decoded.Length == 0 ? null : decoded;
    }

    /// <summary>Last-resort name derived from the MIME type, e.g. <c>video/mp4</c> to <c>download.mp4</c>.</summary>
    public static string FromMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return "download";
        }

        var subtype = mimeType.Split('/').Last().Split(';')[0].Trim();
        subtype = subtype switch
        {
            "mpeg" => "mp3",
            "quicktime" => "mov",
            "x-msvideo" => "avi",
            "x-msdownload" => "exe",
            "x-7z-compressed" => "7z",
            "x-rar-compressed" => "rar",
            "vnd.rar" => "rar",
            "x-tar" => "tar",
            "plain" => "txt",
            "octet-stream" => "bin",
            _ => subtype
        };

        if (subtype.Length is 0 or > 8 || subtype.Any(c => !char.IsLetterOrDigit(c) && c != '+' && c != '-'))
        {
            return "download";
        }

        return "download." + subtype;
    }

    /// <summary>Removes characters Windows rejects and avoids reserved device names.</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "download";
        }

        var value = name;

        // Strip any directory component a server may have injected BEFORE replacing
        // invalid characters. Doing it the other way round would turn the separator
        // into '_' and let the traversal text survive as a confusing file name.
        var separator = value.LastIndexOfAny(new[] { '\\', '/' });
        if (separator >= 0)
        {
            value = value[(separator + 1)..];
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character)
                ? '_'
                : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.', ' ');

        if (result.Length == 0)
        {
            return "download";
        }

        var stem = Path.GetFileNameWithoutExtension(result);
        var extension = Path.GetExtension(result);
        if (ReservedNames.Contains(stem))
        {
            result = "_" + result;
        }

        if (result.Length > MaxLength)
        {
            var keep = Math.Max(1, MaxLength - extension.Length);
            result = stem[..Math.Min(stem.Length, keep)] + extension;
        }

        return result;
    }

    /// <summary>Produces a unique name in <paramref name="directory"/> by appending " (1)", " (2)", ...</summary>
    public static string MakeUnique(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 10_000; index++)
        {
            var next = $"{stem} ({index}){extension}";
            if (!File.Exists(Path.Combine(directory, next)))
            {
                return next;
            }
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }
}
