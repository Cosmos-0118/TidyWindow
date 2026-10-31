using System.ComponentModel;
using System.Diagnostics;

namespace TidyWindow.Next.Core;

/// <summary>An app with a window, as the Lab sees it. <see cref="Priority"/> is null when Windows does not let this user read it, which also means it cannot be changed.</summary>
public sealed record AppProcess(int Id, string Name, string Title, long StartTicks, ProcessPriorityClass? Priority, long MemoryMegabytes);

/// <summary>How much of the processor an app gets first when the processor is busy.</summary>
public enum BoostLevel { Ease, Normal, Boost }

public interface IProcessSource
{
    /// <summary>Apps with a window, started by this user in this session.</summary>
    IReadOnlyList<AppProcess> Windowed();
    ProcessPriorityClass? Priority(int id, long startTicks);
    void SetPriority(int id, long startTicks, ProcessPriorityClass priority);
}

public static class BoostGuard
{
    // Parts of Windows and the security software. Their priority is not for an app to change.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm", "dwm", "fontdrvhost", "svchost", "explorer",
        "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray", "sihost", "ctfmon", "taskmgr", "TidyWindow.Next",
        "textinputhost", "applicationframehost", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "runtimebroker", "widgets", "lockapp"
    };

    public static bool IsProtected(string name) => Protected.Contains(name);

    /// <summary>Real time is never offered: it can freeze the PC. High is the most an app can be given.</summary>
    public static ProcessPriorityClass Class(BoostLevel level) => level switch
    {
        BoostLevel.Ease => ProcessPriorityClass.BelowNormal,
        BoostLevel.Boost => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal
    };
}

/// <summary>The real thing, reading running apps through Windows.</summary>
public sealed class LiveProcesses : IProcessSource
{
    public IReadOnlyList<AppProcess> Windowed()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var list = new List<AppProcess>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId != session || process.MainWindowHandle == IntPtr.Zero || process.Id == Environment.ProcessId) continue;
                var title = process.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new(process.Id, process.ProcessName, title, process.StartTime.Ticks, TryPriority(process), process.WorkingSet64 / (1024 * 1024)));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { /* It closed, or Windows does not allow reading it. */ }
            finally { process.Dispose(); }
        }
        return list;
    }

    public ProcessPriorityClass? Priority(int id, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.StartTime.Ticks == startTicks ? TryPriority(process) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }

    public void SetPriority(int id, long startTicks, ProcessPriorityClass priority)
    {
        using var process = Process.GetProcessById(id);
        // The number can be reused by another program once this one closes, so the start time has to match too.
        if (process.StartTime.Ticks != startTicks) throw new InvalidOperationException("That app has closed.");
        process.PriorityClass = priority;
    }

    private static ProcessPriorityClass? TryPriority(Process process)
    {
        try { return process.PriorityClass; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }
}

/// <summary>
/// Changes how much processor time an app gets first. Windows already favours the window you are using; this matters when the processor is
/// busy with several things at once. It lasts only until the app closes, and each change can be undone while the app is still running.
/// </summary>
public sealed class ProcessBoost(IProcessSource source)
{
    private readonly Dictionary<int, (long Start, ProcessPriorityClass Original)> changed = [];

    public IReadOnlyList<AppProcess> List() =>
        source.Windowed().Where(x => !BoostGuard.IsProtected(x.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True while a change TidyWindow made to this app is still in force.</summary>
    public bool IsChanged(AppProcess app) => changed.TryGetValue(app.Id, out var saved) && saved.Start == app.StartTicks;

    public ActionResult Set(AppProcess app, BoostLevel level)
    {
        if (BoostGuard.IsProtected(app.Name)) return new(false, $"{app.Name} is part of Windows or your security software, so its priority is left alone.");
        if (source.Priority(app.Id, app.StartTicks) is not { } current) return new(false, $"{app.Name} has closed, or Windows does not allow changing it (an app run as administrator needs TidyWindow to run as administrator too).");
        var wanted = BoostGuard.Class(level);
        if (current == wanted) return new(true, $"{app.Name} already has {Describe(wanted)} priority.");
        var original = changed.TryGetValue(app.Id, out var saved) && saved.Start == app.StartTicks ? saved.Original : current;
        try { source.SetPriority(app.Id, app.StartTicks, wanted); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { return new(false, $"Windows did not allow it: {ex.Message}"); }
        if (source.Priority(app.Id, app.StartTicks) != wanted) return new(false, "Windows did not accept the change.");
        if (wanted == original) changed.Remove(app.Id); else changed[app.Id] = (app.StartTicks, original);
        return new(true, level == BoostLevel.Normal ? $"{app.Name} is back to normal." : $"{app.Name} now has {Describe(wanted)} priority until it closes.");
    }

    /// <summary>Puts an app back to the priority it had before TidyWindow changed it.</summary>
    public ActionResult Reset(AppProcess app)
    {
        if (!changed.TryGetValue(app.Id, out var saved) || saved.Start != app.StartTicks) return new(true, $"{app.Name} was not changed.");
        try { source.SetPriority(app.Id, app.StartTicks, saved.Original); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Nothing left to put back if it has closed.
            if (source.Priority(app.Id, app.StartTicks) is null) { changed.Remove(app.Id); return new(true, $"{app.Name} has closed."); }
            return new(false, $"Windows did not allow it: {ex.Message}");
        }
        changed.Remove(app.Id);
        return new(true, $"{app.Name} is back to {Describe(saved.Original)} priority.");
    }

    public int ResetAll(IEnumerable<AppProcess> running)
    {
        var done = 0;
        foreach (var app in running.Where(IsChanged)) if (Reset(app).Ok) done++;
        return done;
    }

    public static string Describe(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.High => "high",
        ProcessPriorityClass.AboveNormal => "above normal",
        ProcessPriorityClass.BelowNormal => "below normal",
        ProcessPriorityClass.Idle => "low",
        ProcessPriorityClass.RealTime => "real time",
        _ => "normal"
    };
}
