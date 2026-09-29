using System.Buffers.Binary;

namespace OpenDLM.NativeHost;

/// <summary>A frame on stdin or stdout could not be decoded.</summary>
internal sealed class NativeMessagingException : Exception
{
    public NativeMessagingException(string message) : base(message)
    {
    }
}

/// <summary>
/// Chrome's native messaging framing: a 4 byte little-endian Int32 byte count
/// followed by exactly that many bytes of UTF-8 JSON. The same framing is used
/// on stdout for the reply.
/// </summary>
internal static class NativeMessaging
{
    /// <summary>
    /// Chromium refuses to deliver (and will not accept) a single message of
    /// 1 MB or more, so frames above this are rejected rather than allocated.
    /// </summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>
    /// Reads one frame. Returns <c>null</c> for a clean end of stream, which is
    /// how Chrome tells the host to shut down.
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerBytes = await ReadUpToAsync(input, header, cancellationToken).ConfigureAwait(false);

        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes < 4)
        {
            throw new NativeMessagingException("Truncated frame header: the stream ended mid-length.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageBytes)
        {
            throw new NativeMessagingException(
                $"Frame length {length} is outside the accepted range 0..{MaxMessageBytes}.");
        }

        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        var payload = new byte[length];
        var payloadBytes = await ReadUpToAsync(input, payload, cancellationToken).ConfigureAwait(false);
        if (payloadBytes != length)
        {
            throw new NativeMessagingException(
                $"Truncated frame body: expected {length} bytes but the stream ended after {payloadBytes}.");
        }

        return payload;
    }

    /// <summary>Writes one frame and flushes it so the browser sees it immediately.</summary>
    public static async Task WriteFrameAsync(Stream output, byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills as much of <paramref name="buffer"/> as the stream can supply.
    /// A pipe read is allowed to return fewer bytes than requested, so a single
    /// ReadAsync call is never enough to frame a message.
    /// </summary>
    private static async Task<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
