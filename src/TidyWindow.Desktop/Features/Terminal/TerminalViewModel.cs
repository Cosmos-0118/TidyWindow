using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>One line of the terminal checklist: what it is, whether it is done, and what the person can do about it.</summary>
public sealed class TerminalRow(string title, string glyph) : ObservableState
{
    private string detail = "", action = "", secondary = "";
    private bool good, canAct, canSecondary;

    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Detail { get => detail; set => Set(ref detail, value); }
    public bool Good { get => good; set { if (Set(ref good, value)) { Changed(nameof(GoodVisibility)); Changed(nameof(PendingVisibility)); Changed(nameof(StateName)); } } }
    public Visibility GoodVisibility => good ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => good ? Visibility.Collapsed : Visibility.Visible;
    public string StateName => Title + (good ? ", done" : ", not done");

    public string Action { get => action; set { if (Set(ref action, value)) { Changed(nameof(ActionVisibility)); Changed(nameof(ActionName)); } } }
    public Visibility ActionVisibility => action.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CanAct { get => canAct; set => Set(ref canAct, value); }
    public string Secondary { get => secondary; set { if (Set(ref secondary, value)) { Changed(nameof(SecondaryVisibility)); Changed(nameof(SecondaryName)); } } }
    public Visibility SecondaryVisibility => secondary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CanSecondary { get => canSecondary; set => Set(ref canSecondary, value); }
    public string ActionName => $"{action}: {Title}";
    public string SecondaryName => $"{secondary}: {Title}";

    internal Func<Task>? Run { get; set; }
    internal Func<Task>? RunSecondary { get; set; }
}

/// <summary>The "Configure your terminal" section of Overview: install what the look needs, apply it, and put the old setup back.</summary>
public sealed class TerminalViewModel : ObservableState, IDisposable
{
    private readonly TerminalSetup setup;
    private readonly ICommandRunner runner;
    private readonly OperationQueueViewModel queue;
    private readonly ActivityLog log;
    private readonly ToastCenter toasts;
    private readonly Func<bool> wingetReady;
    private readonly Func<string, string, string, Task<bool>> confirm;
    private readonly Func<string, Task> open;
    private readonly CancellationTokenSource lifetime = new();
    private TerminalStatus? status;
    private bool busy;
    private string message = "", detail = "";
    private InfoBarSeverity severity = InfoBarSeverity.Informational;

    public TerminalViewModel(TerminalSetup setup, ICommandRunner runner, OperationQueueViewModel queue, ActivityLog log, ToastCenter toasts, Func<bool> wingetReady, Func<string, string, string, Task<bool>> confirm, Func<string, Task> open)
    {
        this.setup = setup; this.runner = runner; this.queue = queue; this.log = log; this.toasts = toasts; this.wingetReady = wingetReady; this.confirm = confirm; this.open = open;
        Apps = new("Command-line apps", "") { Run = InstallAppsAsync, RunSecondary = RemoveAppsAsync };
        Module = new("Fuzzy search for PowerShell", "") { Run = InstallModuleAsync, RunSecondary = RemoveModuleAsync };
        Font = new("Nerd Font", "") { Run = GetFontAsync };
        Look = new("Colours, prompt and shortcuts", "") { Run = ApplyAsync, RunSecondary = RestoreAsync };
        Rows = [Apps, Module, Font, Look];
        Apply(null);
    }

    public TerminalRow Apps { get; }
    public TerminalRow Module { get; }
    public TerminalRow Font { get; }
    public TerminalRow Look { get; }
    public ObservableCollection<TerminalRow> Rows { get; }

    public string Message { get => message; private set { if (Set(ref message, value)) Changed(nameof(MessageVisibility)); } }
    public string Detail { get => detail; private set { if (Set(ref detail, value)) Changed(nameof(DetailVisibility)); } }
    public InfoBarSeverity MessageSeverity => severity;
    public Visibility MessageVisibility => message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Apply(status); } }

    public Task Act(TerminalRow row, bool secondary) => (secondary ? row.RunSecondary : row.Run)?.Invoke() ?? Task.CompletedTask;

    /// <summary>Reads what is installed and applied. Cheap, and safe to call whenever something may have changed.</summary>
    public async Task RefreshAsync()
    {
        TerminalStatus read;
        try { read = await Task.Run(() => setup.Inspect(id => queue.Contains(ManagerKind.Winget, id)), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        Apply(read);
    }

    private void Apply(TerminalStatus? value)
    {
        status = value;
        if (value is null)
        {
            foreach (var row in Rows) { row.Detail = "Checking…"; row.Good = false; row.CanAct = false; row.CanSecondary = false; }
            return;
        }
        var pwsh = value.Missing.All(x => x.Command != "pwsh");
        var queued = value.Missing.Count(x => queue.Contains(ManagerKind.Winget, x.WingetId));

        Apps.Good = value.AllTools;
        Apps.Detail = value.AllTools ? $"All {value.ToolCount} are installed." : $"{value.ToolCount - value.Missing.Count} of {value.ToolCount} installed. Missing: {string.Join(", ", value.Missing.Select(x => x.Name))}.";
        Apps.Action = value.AllTools ? "" : queued == value.Missing.Count ? "In the queue" : $"Install {value.Missing.Count - queued} missing";
        Apps.CanAct = !busy && !value.AllTools && queued < value.Missing.Count && wingetReady();
        Apps.Secondary = value.Added.Count > 0 ? $"Remove {value.Added.Count} added" : "";
        Apps.CanSecondary = !busy && value.Added.Count > 0 && wingetReady() && !value.Added.Any(x => queue.Contains(ManagerKind.Winget, x.WingetId));

        Module.Good = value.PsFzf;
        Module.Detail = value.PsFzf ? "PSFzf is installed. Ctrl+R searches history and Ctrl+T finds files." : "PSFzf adds Ctrl+R history search and Ctrl+T file search to PowerShell 7." + (pwsh ? "" : " It needs PowerShell 7 first.");
        Module.Action = value.PsFzf ? "" : busy && working == nameof(Module) ? "Installing…" : "Install";
        Module.CanAct = !busy && pwsh && !value.PsFzf;
        Module.Secondary = value.ModuleAdded ? "Uninstall" : "";
        Module.CanSecondary = !busy && value.ModuleAdded && pwsh;

        Font.Good = value.Font;
        Font.Detail = value.Font
            ? $"{TerminalKit.FontFace} is installed, so the icons in the prompt and file lists will draw."
            : $"The icons need {TerminalKit.FontFace}. Download CascadiaCode.zip, select the CaskaydiaCoveNerdFontMono files, right-click and choose Install for all users.";
        Font.Action = value.Font ? "" : "Download font";
        Font.CanAct = !value.Font;

        Look.Good = value.LookApplied;
        Look.Detail = value.LookApplied ? "Applied: PowerShell profile, Starship prompt and Windows Terminal theme."
            : "Sets the PowerShell 7 profile, the Starship prompt and the Catppuccin Mocha theme in Windows Terminal." + (value.TerminalFound ? "" : " Open Windows Terminal once to include its theme.")
            + (value.CanRestore ? "" : " Your current files are backed up first.");
        Look.Action = value.LookApplied ? "Applied" : busy && working == nameof(Look) ? "Applying…" : "Apply";
        Look.CanAct = !busy && !value.LookApplied;
        Look.Secondary = value.CanRestore ? "Restore previous" : "";
        Look.CanSecondary = !busy && value.CanRestore;
        if (value.RestorePoint is { } when) Look.Detail += $" Backed up {when.LocalDateTime:d MMM, h:mm tt}.";
    }

    private string working = "";

    private async Task InstallAppsAsync()
    {
        if (status is null || !wingetReady()) return;
        var wanted = status.Missing.Where(x => !queue.Contains(ManagerKind.Winget, x.WingetId)).ToList();
        if (wanted.Count == 0) return;
        // Remembered first, so every app the setup adds can be taken away again, and nothing that was already on the PC ever is.
        setup.RememberInstalled(wanted);
        queue.Enqueue(wanted.Select(x => PackageOperation.Install(x.ToPackage())));
        log.Write(ActivityLevel.Info, ActivityArea.Install, $"Queued {wanted.Count} terminal apps", string.Join(", ", wanted.Select(x => x.WingetId)));
        Show(InfoBarSeverity.Success, $"Queued {wanted.Count} app{(wanted.Count == 1 ? "" : "s")} with WinGet. Follow them in Queue; this list updates when they finish.");
        Apply(status);
        await Task.CompletedTask;
    }

    private async Task RemoveAppsAsync()
    {
        if (status is null || !wingetReady()) return;
        var added = status.Added.Where(x => !queue.Contains(ManagerKind.Winget, x.WingetId)).ToList();
        if (added.Count == 0) return;
        if (!await confirm($"Uninstall {added.Count} app{(added.Count == 1 ? "" : "s")} added by this setup?",
            "Only the apps this setup installed are removed:\n\n" + string.Join(", ", added.Select(x => x.Name)) + "\n\nApps you already had are never touched. Uninstalling runs through the queue, and Windows may ask for permission. If you applied the look, Restore previous first is tidier, though the profile skips missing apps quietly.", "Uninstall")) return;
        queue.Enqueue(added.Select(x => PackageOperation.Manage(new ManagedPackage(ManagerKind.Winget, x.WingetId, x.Name, ""), PackageAction.Uninstall)));
        log.Write(ActivityLevel.Info, ActivityArea.Install, $"Queued removal of {added.Count} terminal apps", string.Join(", ", added.Select(x => x.WingetId)));
        Show(InfoBarSeverity.Success, $"Queued {added.Count} uninstall{(added.Count == 1 ? "" : "s")}. Follow them in Queue; this list updates when they finish.");
        Apply(status);
    }

    private async Task RemoveModuleAsync()
    {
        if (busy) return;
        if (!await confirm("Uninstall PSFzf?", "This removes the PSFzf PowerShell module that this setup installed. Ctrl+R and Ctrl+T fall back to PowerShell's normal behaviour.", "Uninstall")) return;
        working = nameof(Module); Busy = true;
        try { Report(await setup.UninstallPsFzfAsync(runner, null, lifetime.Token), "PSFzf removal"); }
        finally { Busy = false; working = ""; await RefreshAsync(); }
    }

    private async Task InstallModuleAsync()
    {
        if (busy) return;
        working = nameof(Module); Busy = true;
        try
        {
            var result = await setup.InstallPsFzfAsync(runner, null, lifetime.Token);
            Report(result, "PSFzf");
        }
        finally { Busy = false; working = ""; await RefreshAsync(); }
    }

    private Task GetFontAsync() => open(TerminalKit.FontPage);

    private async Task ApplyAsync()
    {
        if (busy || status is null) return;
        var extra = status.Font ? "" : $"\n\nThe {TerminalKit.FontFace} font is not installed yet, so icons will show as empty boxes until it is.";
        if (!await confirm("Apply this terminal look?",
            "This replaces your PowerShell 7 profile and Starship config, and sets the Catppuccin Mocha theme, font and colours in Windows Terminal. Your other Windows Terminal settings stay.\n\nEverything it changes is backed up first. Restore previous puts it all back exactly as it was." + extra, "Apply")) return;
        working = nameof(Look); Busy = true;
        try
        {
            var result = await Task.Run(setup.Apply, lifetime.Token);
            Report(result, "Terminal setup");
        }
        finally { Busy = false; working = ""; await RefreshAsync(); }
    }

    private async Task RestoreAsync()
    {
        if (busy) return;
        if (!await confirm("Restore your previous terminal setup?",
            "This puts back the PowerShell profile, Starship config and Windows Terminal settings as they were before the last time you applied this look. Any edits you made to those files since will be replaced.\n\nThe apps stay installed.", "Restore")) return;
        working = nameof(Look); Busy = true;
        try
        {
            var result = await Task.Run(setup.Restore, lifetime.Token);
            Report(result, "Terminal restore");
        }
        finally { Busy = false; working = ""; await RefreshAsync(); }
    }

    private void Report(TerminalResult result, string what)
    {
        log.Write(result.Ok ? ActivityLevel.Info : ActivityLevel.Warning, ActivityArea.App, result.Message, result.Detail.Length > 0 ? result.Detail : null);
        Show(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, result.Message, result.Detail);
        toasts.Show("terminal:" + what, result.Ok ? ToastKind.Success : ToastKind.Warning, result.Ok ? what + " finished" : what + " needs attention", result.Message);
    }

    private void Show(InfoBarSeverity kind, string text, string more = "")
    {
        severity = kind; Changed(nameof(MessageSeverity));
        Message = text; Detail = more;
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
