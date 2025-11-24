using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TidyWindow.Core.Automation;

internal static class JsonPayloadExtractor
{
    private static readonly byte[] Utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

    public static string? ExtractLastJsonBlock(IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        var composite = BuildCompositeBuffer(lines);
        if (composite.Length == 0)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(composite);
        var span = TrimBom(bytes.AsSpan());
        string? lastPayload = null;
        var index = 0;

        while (index < span.Length)
        {
            var current = span[index];
            if (current != '{' && current != '[')
            {
                index++;
                continue;
            }

            if (!TryExtractJson(span.Slice(index), out var length))
            {
                index++;
                continue;
            }

            lastPayload = Encoding.UTF8.GetString(span.Slice(index, length));
            index += length;
        }

        return string.IsNullOrWhiteSpace(lastPayload) ? null : lastPayload;
    }

    private static string BuildCompositeBuffer(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (line is null)
            {
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static ReadOnlySpan<byte> TrimBom(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == Utf8Bom[0] && data[1] == Utf8Bom[1] && data[2] == Utf8Bom[2])
        {
            return data.Slice(3);
        }

        return data;
    }

    private static bool TryExtractJson(ReadOnlySpan<byte> data, out int length)
    {
        var reader = new Utf8JsonReader(data, isFinalBlock: true, state: default);

        try
        {
            if (!reader.Read())
            {
                length = 0;
                return false;
            }

            if (reader.TokenType is not JsonTokenType.StartObject and not JsonTokenType.StartArray)
            {
                length = 0;
                return false;
            }

            if (!reader.TrySkip())
            {
                length = 0;
                return false;
            }

            length = (int)reader.BytesConsumed;
            return true;
        }
        catch (JsonException)
        {
            length = 0;
            return false;
        }
    }
}
