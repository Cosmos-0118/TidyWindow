using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TidyWindow.App.Services;

/// <summary>
/// Stores recent application log entries for in-app viewing.
/// </summary>
public sealed class ActivityLogService
{
    private const int DefaultCapacity = 500;
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

    public ActivityLogEntry LogInformation(string source, string message, IEnumerable<string>? details = null)
    {
        return AddEntry(ActivityLogLevel.Information, source, message, details);
    }

    public ActivityLogEntry LogSuccess(string source, string message, IEnumerable<string>? details = null)
    {
        return AddEntry(ActivityLogLevel.Success, source, message, details);
    }

    public ActivityLogEntry LogWarning(string source, string message, IEnumerable<string>? details = null)
    {
        return AddEntry(ActivityLogLevel.Warning, source, message, details);
    }

    public ActivityLogEntry LogError(string source, string message, IEnumerable<string>? details = null)
    {
        return AddEntry(ActivityLogLevel.Error, source, message, details);
    }

    private ActivityLogEntry AddEntry(ActivityLogLevel level, string source, string message, IEnumerable<string>? details)
    {
        source = string.IsNullOrWhiteSpace(source) ? "App" : source.Trim();
        message = string.IsNullOrWhiteSpace(message) ? "(no message)" : message.Trim();

        var builder = ImmutableArray.CreateBuilder<string>();
        if (details is not null)
        {
            var count = 0;
            foreach (var line in details)
            {
                if (line is null)
                {
                    continue;
                }

                var normalized = line.Replace("\r", string.Empty, StringComparison.Ordinal).TrimEnd();
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (normalized.Length > 512)
                {
                    normalized = normalized.Substring(0, 512);
                }

                builder.Add(normalized);
                count++;
                if (count >= 200)
                {
                    builder.Add("[truncated]");
                    break;
                }
            }
        }

        var entry = new ActivityLogEntry(DateTimeOffset.UtcNow, level, source, message, builder.ToImmutable());

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
