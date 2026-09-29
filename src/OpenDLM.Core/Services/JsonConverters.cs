using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenDLM.Core.Services;

/// <summary>
/// Serializes <see cref="TimeSpan"/> as "d.HH:mm:ss" (or "HH:mm:ss" without days).
/// Registered explicitly rather than relying on framework support so the on-disk
/// settings format stays stable and hand-editable.
/// </summary>
public sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return TimeSpan.FromSeconds(reader.GetDouble());
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return TimeSpan.Zero;
        }
        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : TimeSpan.Zero;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Days > 0
            ? value.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
    }
}

/// <summary>Same format as <see cref="TimeSpanJsonConverter"/>, for nullable values.</summary>
public sealed class NullableTimeSpanJsonConverter : JsonConverter<TimeSpan?>
{
    private static readonly TimeSpanJsonConverter Inner = new();

    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        return Inner.Read(ref reader, typeof(TimeSpan), options);
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        Inner.Write(writer, value.Value, options);
    }
}
