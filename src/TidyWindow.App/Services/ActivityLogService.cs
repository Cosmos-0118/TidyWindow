using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TidyWindow.App.Services;

/// <summary>
/// Stores recent application log entries for in-app viewing.
/// </summary>
public sealed class ActivityLogService
{
    private const int DefaultCapacity = 500;
    private const int MaxDetailLines = 500;
    private const int MaxDetailDepth = 5;
    private const int MaxLineLength = 4096;

    private static readonly JsonSerializerOptions DetailSerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private readonly object _lock = new();
    private readonly LinkedList<ActivityLogEntry> _entries = new();

    public ActivityLogService(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public event EventHandler<ActivityLogEventArgs>? EntryAdded;

    public IReadOnlyList<ActivityLogEntry> GetSnapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    public ActivityLogEntry LogInformation(string source, string message, IEnumerable<object?>? details = null)
    {
        return AddEntry(ActivityLogLevel.Information, source, message, details);
    }

    public ActivityLogEntry LogSuccess(string source, string message, IEnumerable<object?>? details = null)
    {
        return AddEntry(ActivityLogLevel.Success, source, message, details);
    }

    public ActivityLogEntry LogWarning(string source, string message, IEnumerable<object?>? details = null)
    {
        return AddEntry(ActivityLogLevel.Warning, source, message, details);
    }

    public ActivityLogEntry LogError(string source, string message, IEnumerable<object?>? details = null)
    {
        return AddEntry(ActivityLogLevel.Error, source, message, details);
    }

    private ActivityLogEntry AddEntry(ActivityLogLevel level, string source, string message, IEnumerable<object?>? details)
    {
        source = string.IsNullOrWhiteSpace(source) ? "App" : source.Trim();
        message = string.IsNullOrWhiteSpace(message) ? "(no message)" : message.Trim();

        var normalizedDetails = NormalizeDetails(details);
        var entry = new ActivityLogEntry(DateTimeOffset.UtcNow, level, source, message, normalizedDetails);

        lock (_lock)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }

        EntryAdded?.Invoke(this, new ActivityLogEventArgs(entry));
        return entry;
    }

    private static ImmutableArray<string> NormalizeDetails(IEnumerable<object?>? details)
    {
        if (details is null)
        {
            return ImmutableArray<string>.Empty;
        }

        try
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            var remaining = MaxDetailLines;

            foreach (var fragment in FlattenDetails(details, depth: 0))
            {
                foreach (var line in SplitAndClamp(fragment))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    builder.Add(line);
                    if (--remaining <= 0)
                    {
                        builder.Add("[details truncated]");
                        return builder.ToImmutable();
                    }
                }
            }

            return builder.ToImmutable();
        }
        catch (Exception ex)
        {
            return ImmutableArray.Create($"[detail normalization failed: {ex.Message}]");
        }
    }

    private static IEnumerable<string> FlattenDetails(IEnumerable<object?> details, int depth)
    {
        foreach (var detail in details)
        {
            if (detail is null)
            {
                continue;
            }

            foreach (var formatted in FormatDetailValue(detail, depth))
            {
                yield return formatted;
            }
        }
    }

    private static IEnumerable<string> FormatDetailValue(object value, int depth)
    {
        if (depth > MaxDetailDepth)
        {
            yield return $"[detail depth limit reached for {value.GetType().FullName}]";
            yield break;
        }

        switch (value)
        {
            case string text:
                yield return text;
                yield break;

            case Exception ex:
                yield return ex.ToString();
                yield break;

            case IDictionary dictionary:
                yield return SerializeDictionary(dictionary);
                yield break;

            case IEnumerable enumerable when value is not string:
                foreach (var nested in enumerable.Cast<object?>())
                {
                    if (nested is null)
                    {
                        continue;
                    }

                    foreach (var fragment in FormatDetailValue(nested, depth + 1))
                    {
                        yield return fragment;
                    }
                }

                yield break;

            default:
                yield return SerializeObject(value);
                yield break;
        }
    }

    private static string SerializeDictionary(IDictionary dictionary)
    {
        try
        {
            var normalized = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? "(null)";
                normalized[key] = entry.Value;
            }

            return JsonSerializer.Serialize(normalized, DetailSerializerOptions);
        }
        catch
        {
            return dictionary.ToString() ?? "(dictionary)";
        }
    }

    private static string SerializeObject(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, DetailSerializerOptions);
        }
        catch
        {
            return value.ToString() ?? value.GetType().FullName ?? "(unknown)";
        }
    }

    private static IEnumerable<string> SplitAndClamp(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        var sanitized = value.Replace('\r', '\n');
        var segments = sanitized.Split('\n');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var line = segment.TrimEnd();
            if (line.Length <= MaxLineLength)
            {
                yield return line;
            }
            else
            {
                yield return $"{line[..MaxLineLength]} … (+{line.Length - MaxLineLength} chars)";
            }
        }
    }
}

public sealed record ActivityLogEntry(DateTimeOffset Timestamp, ActivityLogLevel Level, string Source, string Message, ImmutableArray<string> Details);

public enum ActivityLogLevel
{
    Information,
    Success,
    Warning,
    Error
}

public sealed class ActivityLogEventArgs : EventArgs
{
    public ActivityLogEventArgs(ActivityLogEntry entry)
    {
        Entry = entry;
    }

    public ActivityLogEntry Entry { get; }
}
