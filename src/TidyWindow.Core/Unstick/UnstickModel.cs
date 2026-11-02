namespace TidyWindow.Next.Core;

public enum UnstickGroup { Desktop, Input, Network, Hardware }

/// <summary>
/// One quick action. The text says when to use it, what it does and what the person will notice, so nothing is a surprise.
/// <paramref name="Targets"/> are the programs it closes, used to show whether the thing it fixes is running right now.
/// <paramref name="FixId"/> is set for an action that restarts a Windows service. It runs the fix of that id from the Fixes catalog, so it needs
/// administrator permission and can only do what that reviewed fix does.
/// </summary>
public sealed record UnstickAction(
    string Id, UnstickGroup Group, string Name, string Button, string Glyph,
    string When, string Does, string Notice, string Speed,
    IReadOnlyList<string> Targets,
    string? FixId = null)
{
    public bool Admin => FixId is not null;
}

/// <summary>
/// The actions on the Unstick page. Each runs at once, takes a couple of seconds and resets nothing. All but the Hardware group run as the
/// current user and never ask for administrator permission. Anything slower or heavier belongs in Fixes, which runs in the queue.
/// </summary>
public static class UnstickCatalog
{
    private static readonly string[] None = [];

    public static IReadOnlyList<UnstickAction> All { get; } =
    [
        new("restart-explorer", UnstickGroup.Desktop, "Restart File Explorer", "Restart", "E7C4",
            "The taskbar is frozen or will not respond, tray icons are missing, or taskbar previews are stuck.",
            "Closes File Explorer (explorer.exe) for your account and lets Windows start it again.",
            "The taskbar disappears for about two seconds and open folder windows close. Your other apps stay open.",
            "About 2 seconds", ["explorer"]),
        new("restart-start-menu", UnstickGroup.Desktop, "Restart Start and taskbar flyouts", "Restart", "E8FC",
            "Start does nothing when you click it, or Quick Settings, the calendar and the volume and Wi-Fi pop-ups will not open.",
            "Closes StartMenuExperienceHost.exe and ShellExperienceHost.exe for your account. Windows starts fresh ones.",
            "Nothing else changes. Open folder windows and the taskbar stay as they are.",
            "Under 1 second", ["StartMenuExperienceHost", "ShellExperienceHost"]),
        new("restart-search-ui", UnstickGroup.Desktop, "Restart Windows search", "Restart", "E721",
            "The search box opens blank or black, spins forever, or ignores what you type, even though searching itself works.",
            "Closes SearchHost.exe (SearchApp.exe and SearchUI.exe on older Windows) for your account. Windows starts it again.",
            "Nothing changes. The search index is not touched or rebuilt.",
            "Under 1 second", ["SearchHost", "SearchApp", "SearchUI"]),
        new("refresh-display", UnstickGroup.Desktop, "Refresh the display", "Refresh", "E7F4",
            "An app window is black or blank, the screen flickers after sleep or a full-screen game, or a monitor will not redraw.",
            "Presses Win+Ctrl+Shift+B for you, which asks the Desktop Window Manager to start a fresh picture of the desktop.",
            "The screen blinks once and Windows plays a short beep. No windows close.",
            "About 1 second", None),
        new("unstick-previews", UnstickGroup.Desktop, "Unstick folder previews", "Unstick", "E8B9",
            "A folder of photos, videos or PDFs shows a green bar that never finishes, or a file cannot be moved or deleted because it is open in COM Surrogate or Preview Host.",
            "Closes prevhost.exe and only the dllhost.exe that makes thumbnails (the one started with {AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}). Other dllhost.exe programs are left alone.",
            "The locked file is released and thumbnails load again. File Explorer stays open.",
            "Under 1 second", ["prevhost"]),
        new("refresh-associations", UnstickGroup.Desktop, "Refresh icons and variables", "Refresh", "E72C",
            "Icons did not update after you changed a default app, or you changed an environment variable or PATH and want running programs to notice.",
            "Tells Windows that file associations changed (SHChangeNotify) and that the environment changed (WM_SETTINGCHANGE).",
            "Icons may flicker once. Nothing closes.",
            "Under 1 second", None),

        new("unstick-input", UnstickGroup.Input, "Unstick the emoji panel and typing", "Unstick", "E765",
            "Win+. , Win+V or Win+H leaves a frozen pop-up that blocks clicks, or you cannot type into Start, Search or Settings.",
            "Closes TextInputHost.exe and TabTip.exe for your account, and starts ctfmon.exe if it is not running.",
            "A frozen panel disappears at once and typing comes back.",
            "Under 1 second", ["TextInputHost", "TabTip"]),
        new("restart-clipboard", UnstickGroup.Input, "Restart the clipboard", "Restart", "E8C8",
            "Copy and paste stop working, apps hang when you copy, Win+V stops recording, or copying between Remote Desktop and this PC fails.",
            "Checks that the clipboard is free, closes TextInputHost.exe (which draws Win+V), restarts rdpclip.exe if it is running, and restarts your Clipboard User Service by closing its own svchost.exe.",
            "Pinned Win+V items stay. Unpinned history may reset.",
            "About 3 seconds", ["rdpclip"]),
        new("unstick-snipping", UnstickGroup.Input, "Unstick screenshots", "Unstick", "E7C3",
            "Win+Shift+S or Print Screen works once, then stops dimming the screen or never shows the screenshot.",
            "Closes ScreenClippingHost.exe and SnippingTool.exe for your account.",
            "An open Snipping Tool window closes. The next Win+Shift+S works.",
            "Under 1 second", ["ScreenClippingHost", "SnippingTool"]),
        new("restart-notifications", UnstickGroup.Input, "Restart notifications", "Restart", "E7E7",
            "Pop-up notifications stopped appearing, or apps freeze for a few seconds whenever they try to show one.",
            "Stops and starts your Windows Push Notifications User Service (WpnUserService). Windows allows a signed-in user to do this.",
            "A notification on screen closes. New ones appear normally.",
            "About 1 second", None),
        new("unstick-media", UnstickGroup.Input, "Unstick media keys", "Unstick", "E8D6",
            "Play, Pause and Next keys stop controlling your music or video, or Quick Settings keeps showing a tab you closed.",
            "Restarts your Now Playing Session Manager (NPSMSvc) by closing its own svchost.exe, which Windows starts again.",
            "Old media cards disappear. Players show up again when they next play.",
            "About 3 seconds", None),
        new("restart-widgets", UnstickGroup.Input, "Restart Widgets", "Restart", "E71D",
            "The Widgets board opens blank or stays stuck on screen, or Widgets.exe is using a lot of memory or processor time.",
            "Closes Widgets.exe and WidgetService.exe for your account. Windows starts them when you next open Widgets.",
            "Nothing else changes.",
            "Under 1 second", ["Widgets", "WidgetService"]),
        new("clear-sign-in", UnstickGroup.Input, "Clear stuck sign-in prompts", "Clear", "E8D7",
            "A Windows Hello, passkey or work or school sign-in says a remote procedure call is already in progress, or its window never appears.",
            "Closes CredentialUIBroker.exe and Microsoft.AAD.BrokerPlugin.exe for your account. Windows starts them on the next sign-in.",
            "A sign-in prompt that is open closes. Start it again afterwards.",
            "Under 1 second", ["CredentialUIBroker", "Microsoft.AAD.BrokerPlugin"]),

        new("flush-dns", UnstickGroup.Network, "Flush the DNS cache", "Flush", "E774",
            "A website will not load or opens an old address after you changed Wi-Fi or turned a VPN on or off, while other sites work.",
            "Asks the DNS Client service to forget its cached addresses. This needs no administrator permission, unlike ipconfig /flushdns on current Windows.",
            "Nothing visible. Downloads and connections carry on.",
            "Under 1 second", None),
        new("restart-wsl", UnstickGroup.Network, "Restart WSL", "Restart", "E756",
            "vmmemWSL is holding gigabytes of memory, a Linux terminal will not open, or localhost ports stopped forwarding.",
            "Runs wsl.exe --shutdown. Only shown when WSL is installed.",
            "Linux distributions and containers running in WSL stop. The next terminal starts a fresh one.",
            "About 2 seconds", ["wslservice", "vmmemWSL"]),

        new("restart-audio", UnstickGroup.Hardware, "Restart sound", "Restart", "E767",
            "There is no sound, it crackles, or a speaker or headset that worked has disappeared from the list.",
            "Stops and starts the Windows Audio and Windows Audio Endpoint Builder services.",
            "Sound stops for a few seconds. Apps that were playing audio may need to be reopened.",
            "A few seconds", [], "restart-audio"),
        new("restart-bluetooth", UnstickGroup.Hardware, "Restart Bluetooth", "Restart", "E702",
            "Headphones, a mouse or a keyboard shows as connected but does nothing, or will not pair.",
            "Stops and starts the Bluetooth Support service (bthserv).",
            "Bluetooth devices drop for a moment and reconnect by themselves. Nothing is unpaired.",
            "A few seconds", [], "restart-bluetooth"),
        new("restart-camera", UnstickGroup.Hardware, "Restart the camera", "Restart", "E722",
            "The webcam says it is in use by another app, or no camera is found, often after a video call ended badly.",
            "Stops and starts the Windows Camera Frame Server, then asks Windows to look for hardware changes.",
            "An app using the camera loses it and may need to be reopened.",
            "A few seconds", [], "restart-camera"),
        new("clear-print-queue", UnstickGroup.Hardware, "Clear stuck print jobs", "Clear", "E749",
            "A printer says Printing or Deleting forever, or a document cannot be cancelled.",
            "Stops the print service, empties the print queue folder and starts the service again. Printers and drivers are not touched.",
            "Every waiting print job is cancelled.",
            "A few seconds", [], "fix-printing"),
        new("rescan-hardware", UnstickGroup.Hardware, "Rescan hardware", "Rescan", "E772",
            "A USB drive, dock or monitor was plugged in but does not show up.",
            "Asks Windows to look for hardware changes with pnputil /scan-devices, like Scan in Device Manager.",
            "Devices Windows finds are set up as usual. Nothing is removed.",
            "A few seconds", [], "rescan-devices")
    ];

    public static UnstickAction? Find(string id) => All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
}

public readonly record struct EndResult(int Ended, int Denied)
{
    public static EndResult operator +(EndResult a, EndResult b) => new(a.Ended + b.Ended, a.Denied + b.Denied);
}

public enum UserServiceOutcome { NotFound, Restarted, Denied }

/// <summary>What the actions need from Windows, so the logic can be checked without closing anything on a real PC.</summary>
public interface IUnstickSystem
{
    /// <summary>Closes programs with these names that belong to this user's session. <paramref name="commandLine"/> narrows it to ones started with that text.</summary>
    EndResult End(IReadOnlyList<string> programs, string? commandLine = null);
    bool Running(IReadOnlyList<string> programs);
    /// <summary>Starts a program from the Windows folder. False when it could not start.</summary>
    bool Start(string file);
    bool HasProgram(string file);
    /// <summary>True when TidyWindow itself runs as administrator. A program it starts then runs that way too, for the rest of the session, so the runner starts none.</summary>
    bool Elevated { get; }
    Task RestartExplorerAsync();
    void RefreshDisplay();
    void RefreshAssociations();
    /// <summary>Null when the clipboard is free; otherwise the name of whoever is holding it.</summary>
    string? CheckClipboard();
    bool FlushDns();
    /// <summary>Stops and starts a per-user service such as WpnUserService.</summary>
    UserServiceOutcome RestartUserService(string name);
    /// <summary>Restarts a per-user service by closing the process that only it runs in; Windows starts it again.</summary>
    UserServiceOutcome RestartServiceHost(string name);
    /// <summary>Runs a program from the Windows folder and returns its exit code, or -1 if it did not finish in time.</summary>
    Task<int> RunAsync(string file, string arguments, TimeSpan timeout, CancellationToken token);
}

/// <summary>Runs one fix from the Fixes catalog right away, with no output shown. Asks for administrator permission when the fix needs it.</summary>
public delegate Task<PackageActionResult> UnstickFix(FixRun run, IProgress<CommandOutput> progress, CancellationToken token);

/// <summary>Runs one action and says in plain words what happened.</summary>
public sealed class UnstickRunner(IUnstickSystem system, UnstickFix? fixes = null)
{
    /// <summary>The CLSID the thumbnail host dllhost.exe is started with.</summary>
    public const string ThumbnailHost = "{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}";

    public bool Available(UnstickAction action) => action.Id == "restart-wsl" ? system.HasProgram("wsl.exe") : !action.Admin || fixes is not null;

    /// <summary>True when something this action closes is running now. Always false for an action that closes nothing.</summary>
    public bool IsActive(UnstickAction action) => action.Targets.Count > 0 && system.Running(action.Targets);

    public async Task<ActionResult> RunAsync(string id, CancellationToken token = default)
    {
        if (UnstickCatalog.Find(id) is not { } action) return new(false, "That action is not on the page.");
        if (action.FixId is not null) return await RunFixAsync(action, token).ConfigureAwait(false);
        try
        {
            switch (action.Id)
            {
                case "restart-explorer":
                    // Started from an elevated app, File Explorer, and everything opened from Start and the taskbar after it, would run as administrator with no prompts.
                    if (system.Elevated) return new(false, "TidyWindow is running as administrator, so File Explorer was not restarted: it would come back with administrator rights too. Restart it from Task Manager, or sign out and back in.");
                    await system.RestartExplorerAsync().ConfigureAwait(false);
                    return Done("File Explorer restarted. The taskbar is back.");
                case "restart-start-menu":
                    return Close(system.End(action.Targets), "Start and the taskbar flyouts were restarted.", "Start and the taskbar flyouts");
                case "restart-search-ui":
                    return Close(system.End(action.Targets), "Windows search was restarted.", "Windows search");
                case "refresh-display":
                    system.RefreshDisplay();
                    return Done("Asked Windows to redraw the screen. If it blinked, it worked.");
                case "unstick-previews":
                    return Close(system.End(["prevhost"]) + system.End(["dllhost"], ThumbnailHost), "Folder previews were unstuck. Locked files are free again.", "The preview host");
                case "refresh-associations":
                    system.RefreshAssociations();
                    return Done("Icons, file types and environment variables were refreshed.");
                case "unstick-input":
                    return UnstickInput(action);
                case "restart-clipboard":
                    return RestartClipboard();
                case "unstick-snipping":
                    return Close(system.End(action.Targets), "Screenshots were unstuck. Press Win+Shift+S to try.", "The screenshot tool");
                case "restart-notifications":
                    return Service(system.RestartUserService("WpnUserService"), "Notifications were restarted.", "Notifications");
                case "unstick-media":
                    return Service(system.RestartServiceHost("NPSMSvc"), "Media keys were reset. Press play in your player to connect it again.", "The media session service");
                case "restart-widgets":
                    return Close(system.End(action.Targets), "Widgets were restarted. They start when you open the board.", "Widgets");
                case "clear-sign-in":
                    return Close(system.End(action.Targets), "Sign-in prompts were cleared. Start the sign-in again.", "The sign-in helpers");
                case "flush-dns":
                    return system.FlushDns() ? Done("The DNS cache was cleared.") : new(false, "Windows did not clear the DNS cache.");
                case "restart-wsl":
                    return await RestartWslAsync(token).ConfigureAwait(false);
                default:
                    return new(false, "That action is not on the page.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            return new(false, $"Windows did not allow it: {ex.Message}");
        }
    }

    private async Task<ActionResult> RunFixAsync(UnstickAction action, CancellationToken token)
    {
        if (fixes is null) return new(false, "The administrator helper is not available, so this cannot run.");
        PackageActionResult result;
        try { result = await fixes(new(action.FixId!, []), new Silent(), token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, "Administrator permission was declined. Nothing was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return new(false, ex.Message); }
        if (result.Outcome != ActionOutcome.Failed)
            return new(true, result.Outcome == ActionOutcome.Unverified ? $"{action.Name} finished, but a step gave a warning. Fixes shows the details." : Finished(action));
        // The first line of the detail says why: what was declined, or which step stopped.
        var why = result.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return new(false, string.IsNullOrEmpty(why) ? result.Message : why);
    }

    private static string Finished(UnstickAction action) => action.Id switch
    {
        "restart-audio" => "Sound services were restarted.",
        "restart-bluetooth" => "Bluetooth was restarted. Devices reconnect by themselves.",
        "restart-camera" => "The camera service was restarted.",
        "clear-print-queue" => "The print queue was cleared.",
        "rescan-hardware" => "Windows looked for hardware changes.",
        _ => $"{action.Name} finished."
    };

    private sealed class Silent : IProgress<CommandOutput> { public void Report(CommandOutput value) { } }

    private static ActionResult Done(string message) => new(true, message);

    /// <summary>The result of closing programs. Nothing running is not a failure: Windows starts them when they are next needed.</summary>
    private static ActionResult Close(EndResult ended, string done, string what)
    {
        if (ended.Ended > 0) return Done(done);
        if (ended.Denied > 0) return new(false, $"Windows did not let TidyWindow close {what.ToLowerInvariant()}. Restart File Explorer, or sign out and back in.");
        return Done($"{what} was not running, so nothing was stuck. Windows starts it when you next use it.");
    }

    private static ActionResult Service(UserServiceOutcome outcome, string done, string what) => outcome switch
    {
        UserServiceOutcome.Restarted => Done(done),
        UserServiceOutcome.Denied => new(false, $"Windows did not allow restarting that service. {what} may need a sign-out to reset."),
        _ => Done($"{what} was not running, so nothing was stuck. Windows starts it when you next use it.")
    };

    /// <summary>Why a program was not started: TidyWindow runs as administrator, and the program would inherit that for the rest of the session.</summary>
    private static string ElevatedNote(string what, string instead) =>
        $"TidyWindow is running as administrator, so {what} was not started: it would run with administrator rights too. {instead}";

    private ActionResult UnstickInput(UnstickAction action)
    {
        var ended = system.End(action.Targets);
        var started = false; string? held = null;
        if (!system.Running(["ctfmon"]))
        {
            if (system.Elevated) held = ElevatedNote("the keyboard helper (ctfmon.exe)", "Start it from Task Manager, or run TidyWindow normally.");
            else started = system.Start("ctfmon.exe");
        }
        if (ended.Ended == 0 && ended.Denied > 0 && !started) return Close(ended, "", "The text input host");
        if (held is not null) return new(false, (ended.Ended > 0 ? "The typing panels were reset. " : "") + held);
        return Done(started
            ? "The typing panels were reset, and the keyboard helper (ctfmon.exe) was started."
            : ended.Ended > 0 ? "The typing panels were reset." : "Nothing was stuck: the typing panels were not running, and the keyboard helper is running.");
    }

    private ActionResult RestartClipboard()
    {
        var holder = system.CheckClipboard();
        var steps = system.End(["TextInputHost"]);
        string? held = null;
        if (system.Running(["rdpclip"]))
        {
            // Closing it without being able to start it again as the user would leave Remote Desktop with no clipboard at all.
            if (system.Elevated) held = ElevatedNote("the Remote Desktop clipboard (rdpclip.exe)", "It was left running.");
            else
            {
                steps += system.End(["rdpclip"]);
                system.Start("rdpclip.exe");
            }
        }
        var service = system.RestartServiceHost("cbdhsvc");
        if (service == UserServiceOutcome.Denied && steps.Ended == 0)
            return new(false, "Windows did not allow restarting the clipboard service. Sign out and back in to reset it.");
        var message = "The clipboard was restarted.";
        if (holder is not null) message += $" {holder} is holding the clipboard; close it if copying still fails.";
        if (held is not null) message += " " + held;
        return Done(message);
    }

    private async Task<ActionResult> RestartWslAsync(CancellationToken token)
    {
        if (!system.HasProgram("wsl.exe")) return new(false, "WSL is not installed on this PC.");
        var code = await system.RunAsync("wsl.exe", "--shutdown", TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        return code switch
        {
            0 => Done("WSL was shut down. The next terminal starts a fresh one."),
            -1 => new(false, "WSL did not shut down in 30 seconds."),
            _ => new(false, $"WSL reported an error (code {code}).")
        };
    }
}
