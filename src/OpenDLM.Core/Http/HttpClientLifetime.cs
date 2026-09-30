using System.Runtime.CompilerServices;

namespace OpenDLM.Core.Http;

/// <summary>
/// Lets a caller register a client for disposal, so a client built for a single
/// download is released when the engine finishes with it rather than waiting for
/// the garbage collector.
/// </summary>
internal static class HttpClientLifetime
{
    private static readonly ConditionalWeakTable<HttpClient, object> Tracked = new();

    public static void TrackForDisposal(this HttpClient client) => Tracked.Add(client, new object());

    public static void DisposeIfTracked(this HttpClient client)
    {
        if (Tracked.Remove(client))
        {
            client.Dispose();
        }
    }
}
