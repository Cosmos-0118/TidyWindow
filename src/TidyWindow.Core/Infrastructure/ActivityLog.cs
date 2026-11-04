using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
public sealed partial class ActivityLog
{
    private const int MaxMessage = 200, MaxDetail = 4000, RotateBytes = 1 << 20, LookBack = 25, MaxCrashFiles = 20;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly Regex? Personal = PersonalForms();
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

    public ActivityEntry Write(ActivityLevel level, ActivityArea area, string message, string? detail = null) => Write(level, area, message, detail, merge: true);

    /// <summary>
    /// Records a crash with everything a bug report needs. The whole text, with the app version and the Windows build, goes to a file of its own
    /// beside the log (crash-&lt;time&gt;.txt), and the entry points at it; the entry alone would keep only the start and end of a long stack.
    /// Two crashes are never folded into one entry, however alike their messages. Never throws: without a folder, or when the file cannot be
    /// written, the entry carries what it can of the text itself.
    /// </summary>
    public ActivityEntry WriteCrash(string message, string detail)
    {
        var saved = SaveCrash(detail);
        return Write(ActivityLevel.Error, ActivityArea.App, message, saved is null ? detail : $"Details were saved to {saved} in the Logs folder.\n\n{detail}", merge: false);
    }

    private ActivityEntry Write(ActivityLevel level, ActivityArea area, string message, string? detail, bool merge)
    {
        message = Tidy(message, MaxMessage, singleLine: true) ?? "(no message)";
        detail = Tidy(detail, MaxDetail, singleLine: false);
        ActivityEntry entry;
        lock (gate)
        {
            var now = clock();
            var repeat = merge ? FindRepeat(level, area, message, now) : -1;
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

    /// <summary>What a crash file starts with: when, which build of TidyWindow, on which Windows. The same facts a bug report asks for first.</summary>
    public static string Platform()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        return $"TidyWindow {version}\n{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}";
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

    // The crash file is written whole and synchronously: the process is about to end, and a handler that returns before the write lands records nothing.
    private string? SaveCrash(string detail)
    {
        if (Folder is null) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            var stamp = clock().ToLocalTime();
            var name = $"crash-{stamp:yyyyMMdd-HHmmss}.txt";
            for (var n = 2; File.Exists(Path.Combine(Folder, name)); n++) name = $"crash-{stamp:yyyyMMdd-HHmmss}-{n}.txt";
            var text = $"{Platform()}\nTime: {stamp:yyyy-MM-dd HH:mm:ss zzz}\n\n{Scrub(detail.Replace("\r\n", "\n").Replace('\r', '\n')).Trim()}\n";
            File.WriteAllText(Path.Combine(Folder, name), text, new UTF8Encoding(false));
            // Only the newest few are kept; the names sort by time.
            foreach (var old in Directory.GetFiles(Folder, "crash-*.txt").OrderDescending(StringComparer.OrdinalIgnoreCase).Skip(MaxCrashFiles))
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StorageError = ex.Message; return null; }
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
        text = Scrub(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim());
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

    /// <summary>
    /// Replaces the account name wherever a program is likely to have written it: the profile folder in its long, 8.3 (C:\Users\BALATH~1) and
    /// forward-slash forms, and the bare name after a backslash or slash (DOMAIN\name, as whoami and installers print it). A bare name on its own
    /// is left alone, because an account called "Admin" or "Test" would otherwise lose ordinary words from every message.
    /// </summary>
    internal static string Scrub(string text) => Personal is null ? text : Personal.Replace(text, m => m.Groups["path"].Success ? "%USERPROFILE%" : "%USERNAME%");

    private static Regex? PersonalForms()
    {
        var paths = new List<string>();
        void AddPath(string path)
        {
            if (path.Length < 4) return;
            paths.Add(Regex.Escape(path));
            paths.Add(Regex.Escape(path.Replace('\\', '/')));
        }
        AddPath(Profile);
        if (Profile.Length > 0 && OperatingSystem.IsWindows() && ShortPath(Profile) is { } shortProfile && !shortProfile.Equals(Profile, StringComparison.OrdinalIgnoreCase)) AddPath(shortProfile);
        var names = new[] { System.Environment.UserName, Path.GetFileName(Profile) }
            .Where(x => x.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape).ToList();
        var parts = new List<string>();
        if (paths.Count > 0) parts.Add("(?<path>" + string.Join("|", paths) + ")");
        if (names.Count > 0) parts.Add(@"(?<name>(?<=[\\/])(?:" + string.Join("|", names) + @")(?=$|[\\/\s""'.,;:)\]]))");
        if (parts.Count == 0) return null;
        try { return new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static string? ShortPath(string path)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
            return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint length);
}
