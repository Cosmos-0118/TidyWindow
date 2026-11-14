using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TidyWindow.Next.Core;

/// <summary>
/// Tells when Windows is waiting for someone to answer an administrator (UAC) prompt while TidyWindow has work running. A prompt that opens over a busy screen
/// can sit unseen behind other windows or in the taskbar, and the work waits for it. The prompt belongs to <c>consent.exe</c>, whose presence is all that is read:
/// it covers TidyWindow's own requests and the installers a package manager starts, which ask for themselves.
/// </summary>
public sealed class UacPromptWatcher : IDisposable
{
    private readonly Func<bool> promptOpen;
    private readonly Func<bool> active;
    private readonly TimeSpan poll;
    private readonly TimeSpan grace;
    private readonly CancellationTokenSource stop = new();
    private bool shown;

    /// <param name="promptOpen">Whether a prompt is open right now.</param>
    /// <param name="active">Whether TidyWindow is doing work that could have asked for it. A prompt that opens while it is idle belongs to something else and is left alone.</param>
    /// <param name="grace">How long a prompt must stay open before it is reported: one that is answered at once needs no notice.</param>
    public UacPromptWatcher(Func<bool> promptOpen, Func<bool> active, TimeSpan? poll = null, TimeSpan? grace = null)
    {
        this.promptOpen = promptOpen; this.active = active;
        this.poll = poll ?? TimeSpan.FromMilliseconds(500);
        this.grace = grace ?? TimeSpan.FromSeconds(1);
        Loop = RunAsync();
    }

    /// <summary>Raised with true when a prompt has been waiting past the grace time, and with false when it is gone. Raised on a background thread.</summary>
    public event Action<bool>? Changed;

    internal Task Loop { get; }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(poll);
        long? since = null;
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                // Once reported, a prompt is followed until it is gone, even if the work that asked for it has ended.
                var open = Safe(promptOpen) && (shown || Safe(active));
                if (!open)
                {
                    since = null;
                    if (shown) { shown = false; Raise(false); }
                    continue;
                }
                since ??= Stopwatch.GetTimestamp();
                if (!shown && Stopwatch.GetElapsedTime(since.Value) >= grace) { shown = true; Raise(true); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool Safe(Func<bool> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }

    private void Raise(bool open)
    {
        try { Changed?.Invoke(open); }
        catch { /* A display failure must not stop the watching. */ }
    }

    public void Dispose() => stop.Cancel();
}

/// <summary>Windows' administrator prompt, as far as a program without administrator rights can see it.</summary>
public static class UacPrompt
{
    private const string Consent = "consent";

    public static bool IsOpen()
    {
        var found = Process.GetProcessesByName(Consent);
        try { return found.Length > 0; }
        finally { foreach (var process in found) process.Dispose(); }
    }

    /// <summary>
    /// Asks Windows to bring the prompt to the front. When Windows has put the prompt on its own dimmed desktop, no other program can see or reach it, so this finds nothing and returns false
    /// (that prompt is already on screen, and answering it is all there is to do). A prompt waiting on the normal desktop, behind other windows or in the taskbar, is shown.
    /// </summary>
    public static bool TryShow()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var processes = Process.GetProcessesByName(Consent);
        var ids = processes.Select(x => (uint)x.Id).ToHashSet();
        foreach (var process in processes) process.Dispose();
        if (ids.Count == 0) return false;
        var found = false;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (!ids.Contains(owner)) return true;
            found = true;
            if (IsIconic(handle)) ShowWindow(handle, 9 /* SW_RESTORE */);
            SwitchToThisWindow(handle, true);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool altTab);
}
