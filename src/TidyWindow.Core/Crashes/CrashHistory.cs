using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Next.Core;

/// <summary>
/// What earlier scans found, kept on disk so the page opens at once and a scan only has to read what is new. Windows rotates its own logs and forgets old
/// crashes; this keeps them for <see cref="Retention"/>. It holds only crash records, never anything else from the event log.
/// Writing never throws: a disk problem is reported through <see cref="StorageError"/>.
/// </summary>
public sealed class CrashHistory
{
    public const int Capacity = 6000, MaxDismissed = 100;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(120);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();
    private readonly Dictionary<string, CrashEvent> events = [];
    private readonly List<string> dismissed = [];
    private readonly Func<DateTimeOffset> clock;
    private readonly string? file, state;
    private DateTimeOffset? scannedUpTo, seenUpTo;

    /// <param name="folder">Where the history is kept; null keeps it in memory only.</param>
    public CrashHistory(string? folder = null, Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.Now);
        Folder = folder;
        if (folder is null) return;
        file = Path.Combine(folder, "crashes.jsonl"); state = Path.Combine(folder, "state.json");
        Load();
    }

    public string? Folder { get; }
    public string? StorageError { get; private set; }
    public int Count { get { lock (gate) return events.Count; } }
    /// <summary>Everything before this moment has been read. Null until a scan has finished once.</summary>
    public DateTimeOffset? ScannedUpTo { get { lock (gate) return scannedUpTo; } }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<CrashEvent> All() { lock (gate) return events.Values.OrderByDescending(x => x.Time).ToArray(); }

    /// <returns>How many records were new. A record already held (the same event seen by an overlapping scan) is not counted.</returns>
    public int Add(IEnumerable<CrashEvent> found)
    {
        lock (gate)
        {
            var added = 0;
            foreach (var e in found) if (events.TryAdd(e.Id, e)) added++;
            var pruned = Prune();
            if (added > 0 || pruned) WriteEvents();
            return added;
        }
    }

    /// <summary>Everything up to this moment has been looked at by the person. Null until the first time the page is opened or checked.</summary>
    public DateTimeOffset? SeenUpTo { get { lock (gate) return seenUpTo; } }

    /// <summary>Whether the person has closed this observation. The key is its wording, so a changed situation (a new count, another app) shows again.</summary>
    public bool IsDismissed(string key) { lock (gate) return dismissed.Contains(key); }

    public void Dismiss(string key)
    {
        lock (gate)
        {
            if (dismissed.Contains(key)) return;
            dismissed.Add(key);
            if (dismissed.Count > MaxDismissed) dismissed.RemoveAt(0);
            WriteState();
        }
    }

    public void MarkScanned(DateTimeOffset moment)
    {
        lock (gate) { scannedUpTo = moment; WriteState(); }
    }

    public void MarkSeen(DateTimeOffset moment)
    {
        lock (gate) { seenUpTo = moment; WriteState(); }
    }

    /// <summary>Forgets what was saved. Windows' own event log is untouched.</summary>
    public void Clear()
    {
        lock (gate)
        {
            events.Clear(); dismissed.Clear(); scannedUpTo = null;
            foreach (var path in new[] { file, state })
            {
                if (path is null) continue;
                try { File.Delete(path); StorageError = null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StorageError = ex.Message; }
            }
        }
    }

    private bool Prune()
    {
        var before = events.Count;
        var cutoff = clock() - Retention;
        foreach (var old in events.Values.Where(x => x.Time < cutoff).Select(x => x.Id).ToList()) events.Remove(old);
        if (events.Count > Capacity)
            foreach (var old in events.Values.OrderBy(x => x.Time).Take(events.Count - Capacity).Select(x => x.Id).ToList()) events.Remove(old);
        return events.Count != before;
    }

    // The whole file is rewritten through a temporary one, so an interrupted write leaves the previous history, not half of it.
    private void WriteEvents()
    {
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(Folder!);
            var temp = file + ".tmp";
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
                foreach (var e in events.Values.OrderBy(x => x.Time)) writer.WriteLine(JsonSerializer.Serialize(e, Json));
            File.Move(temp, file, true);
            StorageError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StorageError = ex.Message; }
    }

    private void WriteState()
    {
        if (state is null) return;
        try
        {
            Directory.CreateDirectory(Folder!);
            File.WriteAllText(state, JsonSerializer.Serialize(new State(scannedUpTo, seenUpTo, dismissed.ToArray()), Json), new UTF8Encoding(false));
            StorageError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StorageError = ex.Message; }
    }

    private void Load()
    {
        try
        {
            foreach (var line in File.ReadLines(file!))
            {
                try
                {
                    if (JsonSerializer.Deserialize<CrashEvent>(line, Json) is { Id.Length: > 0 } e) events[e.Id] = e;
                }
                catch (JsonException) { /* a torn or hand-edited line is skipped, not fatal */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (ex is not FileNotFoundException and not DirectoryNotFoundException) StorageError = ex.Message; }
        try
        {
            var saved = JsonSerializer.Deserialize<State>(File.ReadAllText(state!), Json);
            scannedUpTo = saved?.ScannedUpTo; seenUpTo = saved?.SeenUpTo;
            if (saved?.Dismissed is { } closed) dismissed.AddRange(closed.Where(x => !string.IsNullOrEmpty(x)).Distinct().TakeLast(MaxDismissed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { if (ex is not FileNotFoundException and not DirectoryNotFoundException and not JsonException) StorageError = ex.Message; }
        Prune();
    }

    private sealed record State(DateTimeOffset? ScannedUpTo, DateTimeOffset? SeenUpTo = null, string[]? Dismissed = null);
}
