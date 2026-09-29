using System.Text.Json.Serialization;

namespace OpenDLM.Core.Models;

/// <summary>
/// One byte range of a download, owned by exactly one worker connection.
/// The engine writes most of these to the resume sidecar file so an interrupted
/// download can continue from where it stopped, even across application restarts.
/// </summary>
public sealed class Segment
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Absolute offset of the first byte this segment owns.</summary>
    [JsonPropertyName("start")]
    public long Start { get; set; }

    /// <summary>Absolute offset of the last byte this segment owns (inclusive).</summary>
    [JsonPropertyName("end")]
    public long End { get; set; }

    /// <summary>Offset of the next byte to write. Equals <see cref="End"/> + 1 when done.</summary>
    [JsonPropertyName("position")]
    public long Position { get; set; }

    [JsonIgnore]
    public long Length => End - Start + 1;

    [JsonIgnore]
    public long BytesWritten => Math.Max(0, Position - Start);

    [JsonIgnore]
    public bool IsComplete => Position > End;

    [JsonIgnore]
    public double Fraction => Length <= 0 ? 0 : Math.Clamp(BytesWritten / (double)Length, 0, 1);

    public void Reset()
    {
        Position = Start;
    }
}
