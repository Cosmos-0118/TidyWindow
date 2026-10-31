using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TidyWindow.Next.Core;

// Only this helper loads the native SDK, so a native failure ends this process and never the app.
// --serve keeps it running and answers one JSON line per request; otherwise it answers a single request given as an argument.
try
{
    var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Everything64.dll"));
    NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => name == "Everything64.dll" ? library : IntPtr.Zero);
}
catch (Exception ex)
{
    // The parent reads this to tell the user what is wrong instead of reporting a bare exit code.
    Console.Error.WriteLine("Everything64.dll could not be loaded: " + ex.Message);
    return 2;
}

var utf8 = new UTF8Encoding(false);
if (args.Contains("--serve"))
{
    using var input = new StreamReader(Console.OpenStandardInput(), utf8);
    using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
    // Ends when the app closes its end of the pipe, so the helper never outlives TidyWindow.
    while (input.ReadLine() is { } line)
    {
        ScoutWorkerRequest? request;
        try { request = JsonSerializer.Deserialize<ScoutWorkerRequest>(line); }
        catch (JsonException) { continue; }
        if (request is null) continue;
        ScoutReply reply;
        try { reply = request.Op == "status" ? ScoutHost.Status() : ScoutHost.Search(request.Request ?? throw new ArgumentException("Missing search request."), request.Fuzzy, inlineFuzzy: false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { reply = new(false, [], Error: ex.Message); }
        output.WriteLine(JsonSerializer.Serialize(new ScoutWorkerReply(request.Id, reply)));
    }
    return 0;
}

Console.OutputEncoding = utf8;
try
{
    var status = ScoutHost.Status();
    if (!status.Ready || args.Length == 0 || args[0] == "--status") { Print(status); return 0; }
    var request = JsonSerializer.Deserialize<ScoutRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(args[0]))) ?? throw new ArgumentException("Missing search request.");
    Print(ScoutHost.Search(request, fuzzyOnly: false, inlineFuzzy: true, trace: args.Contains("--diagnostic") ? Console.Error.WriteLine : null));
    return 0;
}
catch (Exception ex) { Print(new(false, [], Error: ex.Message)); return 0; }

static void Print(ScoutReply value)
{
    var json = JsonSerializer.Serialize(value);
    while (Encoding.UTF8.GetByteCount(json) > 120000 && value.Entries.Count > 0)
    { value = value with { Entries = value.Entries.Take(value.Entries.Count / 2).ToArray(), Limited = true }; json = JsonSerializer.Serialize(value); }
    Console.WriteLine(json);
}

internal static class ScoutHost
{
    private const uint RequestFlags = 0x4 | 0x10 | 0x40; // full path, size, date modified
    private const uint Capacity = 32768;
    private static readonly IntPtr PathBuffer = Marshal.AllocHGlobal((int)Capacity * sizeof(char));
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static ScoutReply Status()
    {
        if (Native.Everything_GetMajorVersion() == 0) return new(false, [], Error: "The default Everything index is unavailable. Start its stable/default instance, or choose a folder to search directly.");
        if (!Native.Everything_IsDBLoaded()) return new(false, [], Error: "Everything is still building its index. Try again shortly.", Indexing: true);
        return new(true, []);
    }

    /// <param name="fuzzyOnly">Runs only the slower search for typos and abbreviations (used by the separate near-match helper).</param>
    /// <param name="inlineFuzzy">Runs that search after the exact one when it found little, as the one-shot helper does.</param>
    public static ScoutReply Search(ScoutRequest request, bool fuzzyOnly, bool inlineFuzzy, Action<string>? trace = null)
    {
        var status = Status();
        if (!status.Ready) return status;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Trace(string stage) => trace?.Invoke($"{clock.ElapsedMilliseconds} ms: {stage}");
        var query = new ScoutQuery(request.Query, Home);
        var candidates = new List<ScoutEntry>();
        var rootPrefix = request.Root is { } selected ? Path.GetFullPath(selected).TrimEnd('\\') + "\\" : null;
        uint total = 0;
        // Both matching modes are set on every query: the SDK keeps the previous query's settings between requests.
        Native.Everything_SetMatchCase(false);
        Native.Everything_SetRequestFlags(RequestFlags);
        Native.Everything_SetOffset(0);

        var homePrefix = Home.Length > 0 ? Home.TrimEnd('\\') + "\\" : null;
        if (!fuzzyOnly)
        {
            // Cheapest queries first. Name matches take about 10 ms; matching inside full paths costs several times that, so it runs only when it can add something.
            if (query.ExactExpression(request.Kind) is { } exact) { Run(exact, matchPath: false, 128, rootPrefix, out _); Trace("exact name"); }
            // Your own files first: a search of the profile cannot be crowded out by millions of system files.
            if (rootPrefix is null && homePrefix is not null) { Run(query.NameExpression(request.Kind, Home), matchPath: false, 1000, homePrefix, out _); Trace("your files"); }
            Run(query.NameExpression(request.Kind, request.Root), matchPath: false, 2000, rootPrefix, out total);
            Trace("names");
            if (query.Terms.Count > 1 || candidates.Count < 100)
            {
                Run(query.PathExpression(request.Kind, request.Root), matchPath: true, 2000, rootPrefix, out var inPaths);
                total = Math.Max(total, inPaths);
                Trace("paths");
            }
        }
        if (fuzzyOnly || inlineFuzzy && candidates.Count < ScoutService.FuzzyBelow && query.Terms.Count <= 3 && !query.HasWildcards && !candidates.Any(x => query.Score(x) >= ScoutQuery.ExactScore))
        {
            if (query.SmartExpression(request.Kind, request.Root) is { } smart)
            {
                Run(smart, matchPath: false, 2000, rootPrefix, out var near);
                total = Math.Max(total, near);
                Trace("close matches");
            }
        }        var ranked = query.Rank(candidates.DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase), request.Kind);
        return new(true, ranked, Math.Max(total, (uint)candidates.Count), total > 2000);

        void Run(string expression, bool matchPath, uint max, string? prefix, out uint count)
        {
            Native.Everything_SetSearchW(expression);
            Native.Everything_SetMatchPath(matchPath);
            Native.Everything_SetMax(max);
            if (!Native.Everything_QueryW(true)) throw new InvalidOperationException("Everything query failed (" + Native.Everything_GetLastError() + ").");
            count = Native.Everything_GetTotResults();
            ReadResults(candidates, prefix);
        }
    }

    private static void ReadResults(List<ScoutEntry> into, string? prefix)
    {
        var count = Native.Everything_GetNumResults();
        for (uint index = 0; index < count; index++)
        {
            var length = Native.Everything_GetResultFullPathNameW(index, PathBuffer, Capacity);
            if (length == 0 || length >= Capacity) continue;
            var path = Marshal.PtrToStringUni(PathBuffer);
            if (path is null || !Path.IsPathFullyQualified(path)) continue;
            if (prefix is not null && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var folder = Native.Everything_IsFolderResult(index);
            long? size = Native.Everything_GetResultSize(index, out var bytes) && bytes >= 0 && !folder ? bytes : null;
            DateTimeOffset? modified = null;
            if (Native.Everything_GetResultDateModified(index, out var date))
                try { modified = DateTimeOffset.FromFileTime(date); } catch (ArgumentOutOfRangeException) { }
            into.Add(new(path, folder, size, modified));
        }
    }
}

internal static class Native
{
    private const string Library = "Everything64.dll";
    [DllImport(Library)] internal static extern uint Everything_GetMajorVersion();
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_IsDBLoaded();
    [DllImport(Library, CharSet = CharSet.Unicode)] internal static extern void Everything_SetSearchW(string value);
    [DllImport(Library)] internal static extern void Everything_SetMatchPath([MarshalAs(UnmanagedType.Bool)] bool value);
    [DllImport(Library)] internal static extern void Everything_SetMatchCase([MarshalAs(UnmanagedType.Bool)] bool value);
    [DllImport(Library)] internal static extern void Everything_SetRequestFlags(uint value);
    [DllImport(Library)] internal static extern void Everything_SetMax(uint value);
    [DllImport(Library)] internal static extern void Everything_SetOffset(uint value);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_QueryW([MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport(Library)] internal static extern uint Everything_GetLastError();
    [DllImport(Library)] internal static extern uint Everything_GetTotResults();
    [DllImport(Library)] internal static extern uint Everything_GetNumResults();
    [DllImport(Library, CharSet = CharSet.Unicode)] internal static extern uint Everything_GetResultFullPathNameW(uint index, IntPtr path, uint capacity);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_IsFolderResult(uint index);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_GetResultSize(uint index, out long size);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_GetResultDateModified(uint index, out long date);
}
