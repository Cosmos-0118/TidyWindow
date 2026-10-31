using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum PackageAction { Update, Uninstall }
public enum ActionOutcome { Completed, RestartRequired, Unverified, Failed }
/// <summary>Where an installed app came from. WinGet also lists what it did not install: registry programs and Store apps, which it can still uninstall.</summary>
public enum AppOrigin { Package, WindowsProgram, StoreApp }
public sealed record ManagedPackage(ManagerKind Manager, string Id, string Name, string Version,
    string? AvailableVersion = null, bool Global = false, bool Held = false, string Note = "", string Source = "")
{
    public string Key => $"{Manager}:{Id}:{Global}";
    public string ManagerName => ManagerDefinition.All.Single(x => x.Kind == Manager).Name;
    public AppOrigin Origin => Manager != ManagerKind.Winget ? AppOrigin.Package
        : Id.StartsWith(@"ARP\", StringComparison.Ordinal) ? AppOrigin.WindowsProgram
        : Id.StartsWith(@"MSIX\", StringComparison.Ordinal) ? AppOrigin.StoreApp : AppOrigin.Package;
    public bool Updatable => Origin == AppOrigin.Package;
}
public sealed record ManagedInventory(ManagerKind Manager, IReadOnlyList<ManagedPackage> Packages, string? Error = null, string? UpdateError = null);
public sealed record PackageActionResult(ActionOutcome Outcome, string Message, string Detail);

/// <summary>Manages exact installed identities. Shares the install/setup operation gate.</summary>
public sealed class InstallationService(PackageService packages, ICommandRunner runner, IExecutableLocator locator, SkippedUpdates? skipped = null)
{
    private readonly SkippedUpdates skipped = skipped ?? new();

    public async Task<ManagedInventory> ReadAsync(ManagerStatus manager, bool checkUpdates = true, CancellationToken token = default)
    {
        var kind = manager.Definition.Kind;
        if (manager.Health != ManagerHealth.Ready || manager.Executable is null)
            return new(kind, [], $"{manager.Definition.Name} is unavailable. Check Overview.");
        try
        {
            IReadOnlyList<ManagedPackage> installed;
            if (kind == ManagerKind.Scoop)
            {
                var list = await runner.RunAsync(ScoopJson(manager, "list", "Name,Version,Source,Info"), token).ConfigureAwait(false);
                if (list.ExitCode == 1 && list.Output.Contains("There aren't any apps installed", StringComparison.OrdinalIgnoreCase)) return new(kind, []);
                if (list.ExitCode != 0) return new(kind, [], Failure(list));
                installed = InstallationOutput.ParseScoop(list.Output, updates: false);
            }
            else
            {
                var inventory = await packages.ReadInstalledAsync(manager, token, windowsApps: true).ConfigureAwait(false);
                if (inventory.Error is not null) return new(kind, [], inventory.Error);
                installed = inventory.Apps.Select(x => new ManagedPackage(kind, x.Id!, x.Name, x.Version, Source: x.Source))
                    .Where(x => !InstallationOutput.IsPlatformComponent(x)).ToArray();
            }
            if (!checkUpdates || installed.Count == 0) return new(kind, installed);
            try
            {
                var command = kind switch
                {
                    ManagerKind.Winget => packages.BuildCommand(kind, manager.Executable,
                        ["list", "--upgrade-available", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"], TimeSpan.FromSeconds(45)),
                    ManagerKind.Chocolatey => packages.BuildCommand(kind, manager.Executable,
                        ["outdated", "--limit-output", "--no-progress"], TimeSpan.FromSeconds(45)),
                    _ => ScoopJson(manager, "status --local", "Name,'Installed Version','Latest Version',Info")
                };
                var result = await runner.RunAsync(command, token).ConfigureAwait(false);
                if (kind == ManagerKind.Winget && result.ExitCode == unchecked((int)0x8A150014)) return new(kind, installed);
                if (result.ExitCode != 0 && !(kind == ManagerKind.Chocolatey && result.ExitCode == 2))
                    return new(kind, installed, UpdateError: Failure(result));
                var updates = kind == ManagerKind.Scoop ? InstallationOutput.ParseScoop(result.Output, updates: true)
                    : InstallationOutput.ParseUpdates(kind, result.Output);
                return new(kind, installed.Select(app =>
                {
                    // Scoop status has no scope column. Reconcile by name AND installed version;
                    // list supplies global scope and hold state for the actual command.
                    var update = updates.FirstOrDefault(x => x.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase) &&
                        (kind != ManagerKind.Scoop || x.Version == app.Version));
                    if (update is null) return app;
                    if (update.AvailableVersion is { Length: > 0 } latest && skipped.IsSkipped(app, latest))
                        return app with { Note = $"{manager.Definition.Name} can’t apply the {latest} update. Update it from the app itself." };
                    return app with { AvailableVersion = update.AvailableVersion, Held = app.Held || update.Held };
                }).ToArray());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return new(kind, installed, UpdateError: ex.Message); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(kind, [], ex.Message); }
    }

    /// <summary>Waits between re-reads. Installers and uninstallers often finish registering their change after the process exits.</summary>
    public IReadOnlyList<TimeSpan> VerifyDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)];

    /// <summary>Reads the inventory, re-reading for a bounded time until <paramref name="settled"/> holds. Returns the last read.</summary>
    internal async Task<ManagedInventory> ReadSettledAsync(ManagerStatus manager, Func<ManagedInventory, bool> settled, CancellationToken token)
    {
        var inventory = await ReadAsync(manager, checkUpdates: false, token).ConfigureAwait(false);
        foreach (var delay in VerifyDelays)
        {
            if (inventory.Error is not null || settled(inventory)) break;
            await Task.Delay(delay, token).ConfigureAwait(false);
            packages.ClearSearchCache();
            inventory = await ReadAsync(manager, checkUpdates: false, token).ConfigureAwait(false);
        }
        return inventory;
    }

    private CommandSpec ScoopJson(ManagerStatus manager, string command, string fields)
    {
        // Select objects before formatting to retain long names, custom manifests, scope and holds.
        var script = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; try { $items = @(& '" +
            manager.Executable!.Replace("'", "''") + "' " + command + " | Select-Object " + fields +
            "); $code=$LASTEXITCODE; Write-Output ('TIDYWINDOW_JSON:' + (ConvertTo-Json -InputObject $items -Compress -Depth 4)); if ($code) { exit $code } } catch { [Console]::Error.WriteLine($_); exit 1 }";
        return new(locator.PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(30));
    }

    public CommandSpec ActionCommand(ManagerStatus manager, ManagedPackage app, PackageAction action, bool interactive = false)
    {
        if (manager.Health != ManagerHealth.Ready || manager.Executable is null || manager.Definition.Kind != app.Manager)
            throw new InvalidOperationException("The package manager is unavailable. Check Overview.");
        if (!Enum.IsDefined(action) || !PackageOutputParser.IsSafeInstalledId(app.Id) || app.Id.Equals("all", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The installed package identifier is incomplete or invalid.");
        if (action == PackageAction.Update && app.Held) throw new InvalidOperationException("This package is pinned or held in its package manager.");
        if (action == PackageAction.Update && !app.Updatable) throw new InvalidOperationException("WinGet did not install this app, so it can’t update it. Use the app itself.");
        var uninstall = action == PackageAction.Uninstall;
        // WinGet's own packages keep their source; a registry program or Store app has none to name, and Store packages are uninstalled from msstore.
        string[] winget = app.Origin != AppOrigin.Package ? []
            : app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? ["--source", "msstore"] : ["--source", "winget"];
        string[] args = app.Manager switch
        {
            ManagerKind.Winget => uninstall
                ? ["uninstall", "--id", app.Id, "--exact", .. winget, "--accept-source-agreements", "--disable-interactivity"]
                : ["upgrade", "--id", app.Id, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            ManagerKind.Chocolatey => [uninstall ? "uninstall" : "upgrade", app.Id, "--yes", "--no-progress"],
            _ => app.Global ? [uninstall ? "uninstall" : "update", app.Id, "--global"] : [uninstall ? "uninstall" : "update", app.Id]
        };
        if (interactive && !uninstall && app.Manager != ManagerKind.Scoop) args = [.. args, app.Manager == ManagerKind.Winget ? "--interactive" : "--not-silent"];
        return packages.BuildCommand(app.Manager, manager.Executable, args, interactive ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(20)) with
        { Elevate = app.Manager == ManagerKind.Chocolatey || app.Manager == ManagerKind.Scoop && app.Global, MayShowInstaller = true };
    }

    public async Task<PackageActionResult> RunAsync(ManagerStatus manager, ManagedPackage app, PackageAction action, CancellationToken token = default, IProgress<CommandOutput>? progress = null, bool interactive = false)
    {
        // Validate before taking the gate, and again against the fresh inventory below.
        ActionCommand(manager, app, action);
        if (!await packages.OperationGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("Another package operation is already running.");
        try
        {
            var before = await ReadAsync(manager, checkUpdates: false, token).ConfigureAwait(false);
            if (before.Error is not null) return new(ActionOutcome.Failed, "Couldn’t verify the installed app", before.Error);
            var current = before.Packages.SingleOrDefault(x => x.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
            if (current is null) return new(ActionOutcome.Unverified, "This app is no longer listed", "Refresh the list before trying again. No command was started.");
            if (action == PackageAction.Update && app.AvailableVersion is { Length: > 0 } target && current.Version == target)
                return new(ActionOutcome.Completed, $"{app.Name} is already up to date", "The queued target version is already installed. No update was started.");
            var result = await runner.RunAsync(ActionCommand(manager, current, action, interactive) with { Progress = progress }, token).ConfigureAwait(false);
            var reason = InstallFailure.Explain(result);
            var detail = $"{current.ManagerName} · {current.Id}\nExit code: {result.ExitCode}\n" + (reason is null ? "" : reason + "\n") + PackageOutputParser.Clean(result.Output + "\n" + result.Error).Trim();
            if (result.ExitCode is not (0 or 1641 or 3010))
            {
                if (action == PackageAction.Update && InstallFailure.UpdateCannotBeApplied(result.ExitCode) && app.AvailableVersion is { Length: > 0 } rejected)
                {
                    skipped.Skip(current, rejected);
                    detail += $"\nTidyWindow won’t offer version {rejected} again. A newer version will be offered when one is released.";
                }
                return new(ActionOutcome.Failed, $"Couldn’t {(action == PackageAction.Update ? "update" : "uninstall")} {app.Name}", detail);
            }
            packages.ClearSearchCache();
            if (result.ExitCode is 1641 or 3010) return new(ActionOutcome.RestartRequired, $"{app.Name}: restart required", detail + "\nRestart Windows to finish applying the change.");
            ManagedPackage? Find(ManagedInventory x) => x.Packages.FirstOrDefault(p => p.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
            var after = await ReadSettledAsync(manager, x => action == PackageAction.Uninstall ? Find(x) is null : Find(x) is { } now && now.Version != current.Version, token).ConfigureAwait(false);
            var remaining = Find(after);
            if (after.Error is not null) return new(ActionOutcome.Unverified, "The manager finished; verification failed", detail + "\n" + after.Error);
            var verified = action == PackageAction.Uninstall ? remaining is null : remaining is not null && remaining.Version != current.Version;
            return verified ? new(ActionOutcome.Completed, $"{app.Name} {(action == PackageAction.Update ? "updated" : "uninstalled")}", detail)
                : new(ActionOutcome.Unverified, action == PackageAction.Update ? $"{app.Name}: no version change detected" : $"{app.Name} is still listed", detail + "\nFollow any installer window, then refresh. The manager’s exit code alone does not confirm the change.");
        }
        finally { packages.OperationGate.Release(); }
    }

    private static string Failure(CommandResult result) => $"Exit {result.ExitCode}: " + PackageOutputParser.Clean(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim();
}

public static partial class InstallationOutput
{
    // Store packages that other apps depend on or that make up Windows itself. Windows' own Installed apps list hides them, and removing one breaks what depends on it.
    [GeneratedRegex(@"^MSIX\\(?:[^_\\]*\.)?(?:NET\.Native|VCLibs|UI\.Xaml|WinAppRuntime|WindowsAppRuntime|DirectX|Services\.Store\.Engagement|Winget\.Source|WidgetsPlatformRuntime|StartExperiencesApp|StorePurchaseApp|Xbox\.TCUI|XboxIdentityProvider|ApplicationCompatibilityEnhancements|Client\.(?:WebExperience|CBS|Core|FileExp|AIX)|Speech\.[\w-]+|OfficePushNotificationUtility|Office\.ActionsServer)[\w.\-]*_", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformStorePackage();

    /// <summary>A Store package that is part of Windows or a library for other apps, not something a person installed to use.</summary>
    public static bool IsPlatformComponent(ManagedPackage package) => package.Origin == AppOrigin.StoreApp && PlatformStorePackage().IsMatch(package.Id);

    public static IReadOnlyList<ManagedPackage> ParseScoop(string output, bool updates)
    {
        var line = PackageOutputParser.Clean(output).Split('\n').LastOrDefault(x => x.StartsWith("TIDYWINDOW_JSON:", StringComparison.Ordinal))
            ?? throw new FormatException("Scoop returned an unreadable inventory. Refresh or check Scoop on Overview.");
        using var document = JsonDocument.Parse(line["TIDYWINDOW_JSON:".Length..]);
        var result = new List<ManagedPackage>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            string Field(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            var name = Field("Name");
            if (!PackageOutputParser.IsSafeId(name)) continue;
            var info = Field("Info");
            var version = Field(updates ? "Installed Version" : "Version");
            var latest = updates ? Field("Latest Version") : "";
            result.Add(new(ManagerKind.Scoop, name, name, version, latest.Length == 0 ? null : latest,
                info.Contains("Global install", StringComparison.OrdinalIgnoreCase), info.Contains("Held package", StringComparison.OrdinalIgnoreCase), info));
        }
        return result;
    }

    public static IReadOnlyList<ManagedPackage> ParseUpdates(ManagerKind manager, string output)
    {
        var lines = PackageOutputParser.Clean(output).Split('\n').Select(x => x.TrimEnd('\r')).ToArray();
        var result = new List<ManagedPackage>();
        if (manager == ManagerKind.Chocolatey)
        {
            foreach (var line in lines)
            {
                var fields = line.Trim().Split('|');
                if (fields.Length == 4 && PackageOutputParser.IsSafeId(fields[0]) && bool.TryParse(fields[3], out var pinned) && fields[2].Length > 0)
                    result.Add(new(manager, fields[0], fields[0], fields[1], fields[2], Held: pinned));
            }
            if (result.Count == 0 && lines.Any(x => x.Contains('|'))) throw new FormatException("Chocolatey’s update list could not be read.");
            return result;
        }
        for (var i = 1; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s*-{2,}[\s-]*$")) continue;
            var columns = Regex.Matches(lines[i - 1], @"\S(?:.*?\S)?(?= {2,}|$)").Select(m => PackageOutputParser.CellWidth(lines[i - 1][..m.Index])).ToArray();
            if (columns.Length < 4) throw new FormatException("WinGet’s update list could not be read.");
            foreach (var row in lines.Skip(i + 1))
            {
                var fields = columns.Select((start, index) => PackageOutputParser.SliceCells(row, start, index + 1 < columns.Length ? columns[index + 1] : int.MaxValue).Trim()).ToArray();
                if (PackageOutputParser.IsSafeId(fields[1]) && fields[0].Length > 0 && fields[3].Length > 0 && !fields[3].Contains('…'))
                    result.Add(new(manager, fields[1], fields[0], fields[2], fields[3]));
            }
            return result.DistinctBy(x => x.Key).ToArray();
        }
        // With nothing to update, WinGet prints a sentence instead of a table (and may still exit 0).
        if (string.IsNullOrWhiteSpace(output) || Regex.IsMatch(string.Join("\n", lines), @"No installed package found|No applicable upgrade|No available upgrade|No newer package versions", RegexOptions.IgnoreCase)) return result;
        throw new FormatException("WinGet’s update list could not be read. No update status was assumed.");
    }
}
