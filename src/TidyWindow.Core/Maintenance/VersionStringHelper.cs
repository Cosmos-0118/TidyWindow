using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Normalizes version strings coming from automation output so comparisons remain reliable.
/// </summary>
public static class VersionStringHelper
{
    private static readonly Regex VersionTokenRegex = new("(?:(?:<=|>=|<|>|=)\\s*)?(?<v>\\d+(?:[\\.\\-_]\\d+){0,4})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "not installed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = VersionTokenRegex.Match(trimmed);
        if (match.Success && TryNormalize(match.Groups["v"].Value, out var normalized))
        {
            return normalized;
        }

        if (TryNormalize(trimmed, out normalized))
        {
            return normalized;
        }

        return trimmed;
    }

    private static bool TryNormalize(string candidate, out string normalized)
    {
        var sanitized = Sanitize(candidate);
        if (sanitized.Length == 0)
        {
            normalized = string.Empty;
            return false;
        }

        if (Version.TryParse(sanitized, out var parsed))
        {
            normalized = parsed.ToString();
            return true;
        }

        normalized = sanitized;
        return char.IsDigit(sanitized[0]);
    }

    private static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var buffer = text.Trim()
            .Replace('_', '.')
            .Replace('-', '.')
            .Trim();

        buffer = Regex.Replace(buffer, "[^0-9.]", ".");
        while (buffer.Contains("..", StringComparison.Ordinal))
        {
            buffer = buffer.Replace("..", ".", StringComparison.Ordinal);
        }

        buffer = buffer.Trim('.');
        if (buffer.Length == 0)
        {
            return string.Empty;
        }

        var segments = buffer.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        if (segments.Length > 4)
        {
            segments = segments.Take(4).ToArray();
        }

        return string.Join('.', segments);
    }
}
