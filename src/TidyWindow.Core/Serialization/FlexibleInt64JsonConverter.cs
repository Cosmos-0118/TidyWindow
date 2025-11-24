using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Core.Serialization;

/// <summary>
/// Allows script payloads to emit numbers either as JSON numbers or as quoted strings.
/// Non-numeric strings are treated as <c>null</c> rather than throwing.
/// </summary>
internal sealed class FlexibleInt64JsonConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var numericValue))
                {
                    return numericValue;
                }

                // Ignore numbers that exceed Int64 precision instead of throwing.
                return null;
            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                // Gracefully skip non-numeric string payloads.
                return null;
            default:
                throw new JsonException($"Unexpected token '{reader.TokenType}' for Int64 conversion.");
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(value.Value);
    }
}
