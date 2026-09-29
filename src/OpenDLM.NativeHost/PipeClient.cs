using System.IO.Pipes;

namespace OpenDLM.NativeHost;

/// <summary>
/// No OpenDLM process is listening on the IPC pipe. This is the only failure
/// that is safe to retry: if the connect itself was refused, the app cannot
/// possibly have seen the request, so re-sending it cannot duplicate work.
/// </summary>
internal sealed class PipeUnavailableException : Exception
{
    public PipeUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The pipe connected but the exchange did not complete correctly. This is NOT
/// retried, because the app may already have accepted the request.
/// </summary>
internal sealed class PipeProtocolException : Exception
{
    public PipeProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Client side of the OpenDLM IPC protocol: one request in, one response out,
/// per connection, both framed exactly like a native messaging frame.
/// </summary>
internal static class PipeClient
{
    public const string PipeName = "OpenDLM.ipc";

    /// <summary>How long a single connect attempt may take before it is treated as "not running".</summary>
    private const int ConnectTimeoutMs = 400;

    /// <summary>Upper bound on waiting for the app's reply, so the browser is never left hanging.</summary>
    private const int ResponseTimeoutMs = 60_000;

    /// <summary>
    /// Sends one UTF-8 JSON request and returns the raw UTF-8 bytes of the reply.
    /// </summary>
    /// <exception cref="PipeUnavailableException">The app is not listening.</exception>
    /// <exception cref="PipeProtocolException">The app answered incorrectly or not at all.</exception>
    public static async Task<byte[]> RelayAsync(byte[] requestUtf8, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestUtf8);

        NamedPipeClientStream pipe;
        try
        {
            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        }
        catch (Exception ex)
        {
            throw new PipeUnavailableException("Could not create the IPC pipe client.", ex);
        }

        await using (pipe)
        {
            try
            {
                await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PipeUnavailableException("Nothing is listening on the OpenDLM IPC pipe.", ex);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ResponseTimeoutMs);

            try
            {
                await NativeMessaging.WriteFrameAsync(pipe, requestUtf8, deadline.Token).ConfigureAwait(false);

                var reply = await NativeMessaging.ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false);
                if (reply is null)
                {
                    throw new PipeProtocolException("OpenDLM closed the connection without sending a reply.");
                }

                return reply;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PipeProtocolException("Timed out waiting for OpenDLM to reply.", ex);
            }
            catch (NativeMessagingException ex)
            {
                throw new PipeProtocolException("OpenDLM sent a frame the host could not decode.", ex);
            }
            catch (IOException ex)
            {
                throw new PipeProtocolException("The IPC connection to OpenDLM failed mid-exchange.", ex);
            }
        }
    }
}
