using System.Diagnostics;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>A copy of the app that ended without closing properly.</summary>
public sealed record EndedSession(int ProcessId, DateTimeOffset Started, string? Path);

/// <summary>
/// Notices when the app did not close properly last time. A crash inside Windows or the .NET runtime ends the process before any
/// handler can write to the activity log, so the only trace is an absence: each running copy keeps a small marker file named after its
/// process id and removes it on a normal exit. A marker whose process is gone means that copy crashed, was killed, or lost power.
/// </summary>
public sealed class SessionGuard
{
    private static readonly JsonSerializerOptions Json = new();
    private readonly string folder;
    private readonly ActivityLog log;
    private readonly Func<int, DateTimeOffset?> startOf;
    private readonly Func<DateTimeOffset> clock;
    private string? mine;

    /// <param name="folder">Where the markers are kept.</param>
    /// <param name="log">Where a copy that ended without cleaning up is reported.</param>
    /// <param name="startOf">When the process with this id started, or null if there is none. Replaceable in tests.</param>
    public SessionGuard(string folder, ActivityLog log, Func<int, DateTimeOffset?>? startOf = null, Func<DateTimeOffset>? clock = null)
    {
        this.folder = folder;
        this.log = log;
        this.startOf = startOf ?? ProcessStart;
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Reports the copies that ended without cleaning up, then records this copy as running until the process exits normally.
    /// Never throws: a guard that fails must not stop the app.
    /// </summary>
    public IReadOnlyList<EndedSession> Begin()
    {
        var ended = new List<EndedSession>();
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                var marker = Read(file);
                // A marker that cannot be read says nothing useful, so it is dropped rather than reported.
                if (marker is null) { TryDelete(file); continue; }
                // Another copy that is still running (a debug build next to an installed one) is not a crash. A process id can be
                // reused by something else after a crash, which shows as a different start time.
                if (startOf(marker.ProcessId) is { } started && Math.Abs((started - marker.Started).TotalSeconds) < 5) continue;
                ended.Add(marker);
                TryDelete(file);
            }
            var id = Environment.ProcessId;
            mine = Path.Combine(folder, id + ".json");
            var now = startOf(id) ?? clock();
            File.WriteAllText(mine, JsonSerializer.Serialize(new EndedSession(id, now, Environment.ProcessPath), Json));
            AppDomain.CurrentDomain.ProcessExit += (_, _) => End();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { mine = null; }
        foreach (var session in ended) Report(session);
        return ended;
    }

    /// <summary>Marks this copy as closed properly.</summary>
    public void End()
    {
        if (mine is { } file) TryDelete(file);
        mine = null;
    }

    private void Report(EndedSession session) => log.Write(ActivityLevel.Warning, ActivityArea.App, "TidyWindow closed unexpectedly last time",
        $"The copy started {session.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} ended without closing properly" +
        (session.Path is null ? "" : $" ({session.Path})") +
        ". To see why, check Event Viewer, Windows Logs, Application, around that time.");

    private static EndedSession? Read(string file)
    {
        try { return JsonSerializer.Deserialize<EndedSession>(File.ReadAllText(file), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static DateTimeOffset? ProcessStart(int id)
    {
        try { using var process = Process.GetProcessById(id); return new DateTimeOffset(process.StartTime); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
}
