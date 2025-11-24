using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Core.Serialization;

/// <summary>
/// Allows JSON payloads to emit either a string or an array of strings for list-based fields.
/// </summary>
internal sealed class FlexibleStringListJsonConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                var single = reader.GetString();
                return string.IsNullOrWhiteSpace(single) ? new List<string>() : new List<string> { single };
            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        return list;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var value = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            list.Add(value);
                        }

                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        continue;
                    }

                    throw new JsonException("Expected string entries inside the array.");
                }

                throw new JsonException("Unterminated array while reading string list.");
            default:
                throw new JsonException($"Unexpected token '{reader.TokenType}' for string list conversion.");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var entry in value)
        {
            if (entry is not null)
            {
                writer.WriteStringValue(entry);
            }
        }

        writer.WriteEndArray();
    }
}
