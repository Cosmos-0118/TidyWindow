using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TidyWindow.Next.Core;

// Only this short-lived worker loads the native SDK. Parent cancellation can terminate it safely.
try
{
    var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Everything64.dll"));
    NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => name == "Everything64.dll" ? library : IntPtr.Zero);
    if (Native.Everything_GetMajorVersion() == 0) { Reply(new(false, [], Error: "The default Everything index is unavailable. Start its stable/default instance, or choose a folder to search directly.")); return; }
    if (!Native.Everything_IsDBLoaded()) { Reply(new(false, [], Error: "Everything is still building its index. Try again shortly.", Indexing: true)); return; }
    if (args.Length == 0 || args[0] == "--status") { Reply(new(true, [])); return; }
    var request = JsonSerializer.Deserialize<ScoutRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(args[0]))) ?? throw new ArgumentException("Missing search request.");
    var query = new ScoutQuery(request.Query);
    var diagnostic = args.Contains("--diagnostic");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    void Trace(string stage) { if (diagnostic) Console.Error.WriteLine($"{clock.ElapsedMilliseconds} ms: {stage}"); }
    Native.Everything_SetSearchW(query.EverythingExpression(request.Kind, request.Root));
    Native.Everything_SetMatchPath(true);
    Native.Everything_SetMatchCase(false);
    Native.Everything_SetRequestFlags(0x4 | 0x10 | 0x40);
    Native.Everything_SetMax(2000);
    var candidates = new List<ScoutEntry>();
    var rootPrefix = request.Root is { } selectedRoot ? Path.GetFullPath(selectedRoot).TrimEnd('\\') + "\\" : null;
    // Protect exact names from a broad query's alphabetical candidate cap.
    Native.Everything_SetSearchW((request.Kind == ScoutItemKind.Files ? "file: " : request.Kind == ScoutItemKind.Folders ? "folder: " : "") + "regex:\"^" + System.Text.RegularExpressions.Regex.Escape(request.Query.Trim().Trim('"')).Replace("\"", "\\x22") + "$\"");
    Native.Everything_SetMatchPath(false); Native.Everything_SetMax(128);
    Trace("exact query");
    if (Native.Everything_QueryW(true)) ReadCandidates();
    Trace("exact complete");
    Native.Everything_SetSearchW(query.EverythingExpression(request.Kind, request.Root));
    Native.Everything_SetMatchPath(true); Native.Everything_SetMax(2000);
    Trace("broad query");
    if (!Native.Everything_QueryW(true)) { Reply(new(false, [], Error: "Everything query failed (" + Native.Everything_GetLastError() + ").")); return; }
    var total = Native.Everything_GetTotResults();
    ReadCandidates();
    Trace("broad complete");
    if (candidates.Count < 25 && query.Terms.Count <= 3 && !candidates.Any(x => query.Score(x) == ScoutQuery.ExactScore))
    {
        Native.Everything_SetSearchW(query.EverythingExpression(request.Kind, request.Root, smart: true));
        // Fuzzy matching is for names. Running abbreviation regexes across millions of full paths is expensive.
        Native.Everything_SetMatchPath(false);
        Trace("smart name query");
        if (Native.Everything_QueryW(true)) { total = Native.Everything_GetTotResults(); ReadCandidates(); }
        Trace("smart complete");
    }
    Reply(new(true, query.Rank(candidates.DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase), request.Kind), Math.Max(total, (uint)candidates.Count), total > 2000));
    void ReadCandidates()
    {
    // Reuse one native buffer instead of allocating/marshalling a 64 KiB StringBuilder per result.
    const uint capacity = 32768;
    var buffer = Marshal.AllocHGlobal((int)capacity * sizeof(char));
    try
    {
    for (uint index = 0; index < Native.Everything_GetNumResults(); index++)
    {
        var pathLength = Native.Everything_GetResultFullPathNameW(index, buffer, capacity);
        if (pathLength == 0 || pathLength >= capacity) continue;
        var path = Marshal.PtrToStringUni(buffer);
        if (path is null || !Path.IsPathFullyQualified(path)) continue;
        if (rootPrefix is not null && !path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
        var folder = Native.Everything_IsFolderResult(index);
        long? size = Native.Everything_GetResultSize(index, out var length) && length >= 0 && !folder ? length : null;
        DateTimeOffset? modified = null;
        if (Native.Everything_GetResultDateModified(index, out var date))
            try { modified = DateTimeOffset.FromFileTime(date); } catch (ArgumentOutOfRangeException) { }
        candidates.Add(new(path, folder, size, modified));
    }
    }
    finally { Marshal.FreeHGlobal(buffer); }
    }
}
catch (Exception ex) { Reply(new(false, [], Error: ex.Message)); }

static void Reply(ScoutReply value)
{
    var json = JsonSerializer.Serialize(value);
    while (Encoding.UTF8.GetByteCount(json) > 120000 && value.Entries.Count > 0)
    { value = value with { Entries = value.Entries.Take(value.Entries.Count / 2).ToArray(), Limited = true }; json = JsonSerializer.Serialize(value); }
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.WriteLine(json);
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
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_QueryW([MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport(Library)] internal static extern uint Everything_GetLastError();
    [DllImport(Library)] internal static extern uint Everything_GetTotResults();
    [DllImport(Library)] internal static extern uint Everything_GetNumResults();
    [DllImport(Library, CharSet = CharSet.Unicode)] internal static extern uint Everything_GetResultFullPathNameW(uint index, IntPtr path, uint capacity);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_IsFolderResult(uint index);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_GetResultSize(uint index, out long size);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Everything_GetResultDateModified(uint index, out long date);
}
