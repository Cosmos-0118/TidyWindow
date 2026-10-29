using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Next.Core;

public enum ActivityLevel { Info, Success, Warning, Error }
public enum ActivityArea { App, Managers, Search, Install }

public sealed record ActivityEntry(string Id, DateTimeOffset Time, ActivityLevel Level, ActivityArea Area, string Message, string? Detail = null, int Count = 1);

/// <summary>Narrows entries by minimum severity, area and text. The default matches everything.</summary>
public sealed record ActivityFilter(ActivityLevel MinLevel = ActivityLevel.Info, ActivityArea? Area = null, string Text = "")
{
    public bool Matches(ActivityEntry entry)
    {
        var text = Text.Trim();
        return entry.Level >= MinLevel && (Area is null || entry.Area == Area) &&
            (text.Length == 0 || entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase) || entry.Detail?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>
/// The app's record of things worth knowing: outcomes, state changes and failures. It is not a trace log.
/// An identical event repeated within a few hours updates one entry's count instead of adding another, so a
/// persistent problem stays a single line. Entries are bounded in memory and stored as JSON lines on disk.
/// Writing never throws: a disk problem is reported through <see cref="StorageError"/>.
/// </summary>
public sealed class ActivityLog
{
    private const int MaxMessage = 200, MaxDetail = 4000, RotateBytes = 1 << 20, LookBack = 25;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly object gate = new();
    private readonly List<ActivityEntry> entries = [];
    private readonly int capacity;
    private readonly Func<DateTimeOffset> clock;
    private readonly string? file, previous;

    /// <param name="folder">Where entries are stored; null keeps them in memory only.</param>
    public ActivityLog(string? folder = null, int capacity = 500, Func<DateTimeOffset>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity; this.clock = clock ?? (() => DateTimeOffset.Now);
        Folder = folder;
        if (folder is null) return;
        file = Path.Combine(folder, "activity.jsonl"); previous = Path.Combine(folder, "activity.previous.jsonl");
        Load();
    }

    public string? Folder { get; }
    public string? StorageError { get; private set; }
    public int Count { get { lock (gate) return entries.Count; } }
    public event Action<ActivityEntry>? Written;
    public event Action? Cleared;

    /// <summary>Entries from oldest to newest.</summary>
    public IReadOnlyList<ActivityEntry> Snapshot() { lock (gate) return entries.ToArray(); }

    public ActivityEntry Write(ActivityLevel level, ActivityArea area, string message, string? detail = null)
    {
        message = Tidy(message, MaxMessage, singleLine: true) ?? "(no message)";
        detail = Tidy(detail, MaxDetail, singleLine: false);
        ActivityEntry entry;
        lock (gate)
        {
            var now = clock();
            var repeat = FindRepeat(level, area, message, now);
            if (repeat >= 0)
            {
                var old = entries[repeat];
                entry = old with { Time = now, Detail = detail ?? old.Detail, Count = old.Count + 1 };
                entries.RemoveAt(repeat);
            }
            else entry = new(Guid.NewGuid().ToString("N")[..12], now, level, area, message, detail);
            entries.Add(entry);
            if (entries.Count > capacity) entries.RemoveRange(0, entries.Count - capacity);
            Persist(entry);
        }
        Raise(entry);
        return entry;
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            foreach (var path in new[] { file, previous })
            {
                if (path is null) continue;
                try { File.Delete(path); StorageError = null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StorageError = ex.Message; }
            }
        }
        try { Cleared?.Invoke(); } catch { /* a subscriber must not break the caller */ }
    }

    /// <summary>A plain-text transcript suitable for a support request.</summary>
    public static string Format(IEnumerable<ActivityEntry> items)
    {
        var text = new StringBuilder();
        foreach (var e in items)
        {
            text.Append($"[{e.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {e.Level.ToString().ToUpperInvariant(),-7} {e.Area,-8} {e.Message}");
            if (e.Count > 1) text.Append($" (x{e.Count})");
            text.AppendLine();
            if (e.Detail is not null)
                foreach (var line in e.Detail.Split('\n')) text.AppendLine("    " + line.TrimEnd());
        }
        return text.ToString();
    }

    private int FindRepeat(ActivityLevel level, ActivityArea area, string message, DateTimeOffset now)
    {
        for (var i = entries.Count - 1; i >= Math.Max(0, entries.Count - LookBack); i--)
        {
            var e = entries[i];
            if (now - e.Time > RepeatWindow) break;
            if (e.Level == level && e.Area == area && e.Message == message) return i;
        }
        return -1;
    }

    private void Raise(ActivityEntry entry)
    {
        if (Written is null) return;
        foreach (var handler in Written.GetInvocationList().Cast<Action<ActivityEntry>>())
            try { handler(entry); } catch { /* a subscriber must not break the caller */ }
    }

    // Each line is a complete entry. A repeat appends its updated entry; the latest line for an Id wins on load.
    private void Persist(ActivityEntry entry)
    {
        if (file is null || previous is null) return;
        try
        {
            Directory.CreateDirectory(Folder!);
            if (File.Exists(file) && new FileInfo(file).Length > RotateBytes) File.Move(file, previous, true);
            File.AppendAllText(file, JsonSerializer.Serialize(entry, Json) + "\n", new UTF8Encoding(false));
            StorageError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StorageError = ex.Message; }
    }

    private void Load()
    {
        var latest = new Dictionary<string, (long Order, ActivityEntry Entry)>();
        long order = 0;
        foreach (var path in new[] { previous!, file! })
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { if (ex is not FileNotFoundException and not DirectoryNotFoundException) StorageError = ex.Message; continue; }
            foreach (var line in lines)
            {
                try
                {
                    var e = JsonSerializer.Deserialize<ActivityEntry>(line, Json);
                    if (e is null || string.IsNullOrEmpty(e.Id) || string.IsNullOrEmpty(e.Message)) continue;
                    latest[e.Id] = (order++, e with { Count = Math.Max(1, e.Count) });
                }
                catch (JsonException) { /* a torn or hand-edited line is skipped, not fatal */ }
            }
        }
        entries.AddRange(latest.Values.OrderBy(x => x.Order).Select(x => x.Entry).TakeLast(capacity));
    }

    // Keeps shared logs free of the Windows account name and bounds what one event can store.
    private static string? Tidy(string? text, int max, bool singleLine)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (Profile.Length > 0) text = text.Replace(Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\n' && !singleLine || c == '\t') clean.Append(c);
            else if (char.IsControl(c) || singleLine && c == '\n') clean.Append(' ');
            else clean.Append(c);
        }
        text = clean.ToString();
        if (text.Length <= max) return text;
        // Errors usually name the cause at the start and the failure at the end; keep both.
        return singleLine ? text[..(max - 1)] + "…" : text[..(max * 3 / 8)] + "\n…\n" + text[^(max * 5 / 8)..];
    }
}
