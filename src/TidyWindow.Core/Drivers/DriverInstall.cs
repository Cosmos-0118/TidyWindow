namespace TidyWindow.Next.Core;

/// <summary>The few <c>pnputil</c> commands the driver helper uses. Each takes names that already passed <see cref="DriverGuard"/>; there is no way to hand it free text.</summary>
public interface IPnpUtil
{
    /// <summary>Copies an installed driver package out of Windows' driver store into <paramref name="folder"/>.</summary>
    Task<CommandResult> ExportAsync(string infName, string folder, CancellationToken cancellationToken);
    /// <summary>Puts a driver package into the driver store without installing it on a device.</summary>
    Task<CommandResult> AddAsync(string infPath, CancellationToken cancellationToken);
    /// <summary>Removes a driver package from devices that use it and from the store. Windows then picks the best driver left.</summary>
    Task<CommandResult> DeleteAsync(string infName, CancellationToken cancellationToken);
    Task<CommandResult> ScanAsync(CancellationToken cancellationToken);
}

public sealed class PnpUtil(ICommandRunner runner) : IPnpUtil
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
    private static string Program => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

    private Task<CommandResult> Run(CancellationToken cancellationToken, params string[] arguments) => runner.RunAsync(new CommandSpec(Program, arguments, Limit), cancellationToken);

    public Task<CommandResult> ExportAsync(string infName, string folder, CancellationToken cancellationToken) =>
        DriverGuard.IsOemInf(infName) ? Run(cancellationToken, "/export-driver", infName, folder) : throw new ArgumentException("Only a driver Windows copied into its store can be exported.", nameof(infName));

    public Task<CommandResult> AddAsync(string infPath, CancellationToken cancellationToken) =>
        string.Equals(Path.GetExtension(infPath), ".inf", StringComparison.OrdinalIgnoreCase) ? Run(cancellationToken, "/add-driver", infPath) : throw new ArgumentException("That is not a driver file.", nameof(infPath));

    public Task<CommandResult> DeleteAsync(string infName, CancellationToken cancellationToken) =>
        DriverGuard.IsOemInf(infName) ? Run(cancellationToken, "/delete-driver", infName, "/uninstall", "/force") : throw new ArgumentException("Only a driver Windows copied into its store can be removed.", nameof(infName));

    public Task<CommandResult> ScanAsync(CancellationToken cancellationToken) => Run(cancellationToken, "/scan-devices");
}

/// <summary>
/// The copies of replaced drivers. The record and the exported files live in a folder only administrators can change, so a program running as the
/// ordinary account cannot plant a driver for the helper to put back with administrator rights. The helper is given a backup's id and nothing else.
/// </summary>
public sealed class DriverBackups(ProtectedStore store)
{
    public string FolderFor(string id) => DriverGuard.IsBackupId(id) ? Path.Combine(store.Folder, "backup-" + id) : throw new ArgumentException("That is not a saved-driver id.", nameof(id));

    public void Save(DriverBackupRecord record) => store.Save(record.Id, record);

    public DriverBackupRecord? Load(string id) => DriverGuard.IsBackupId(id) ? store.Load<DriverBackupRecord>(id) : null;

    public void Delete(string id)
    {
        if (!DriverGuard.IsBackupId(id)) return;
        try { store.Delete(id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { var folder = FolderFor(id); if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Installs one driver from Windows Update, and takes it back out. This is the part that runs with administrator rights, so it trusts nothing it is told except an
/// id: it asks Windows Update itself, installs only what Windows Update just offered, refuses a driver older than the one in use, saves the current driver
/// before changing anything, and reads the PC back afterwards.
/// </summary>
public sealed class DriverInstaller(IDriverUpdates updates, IInstalledDrivers installed, DriverBackups backups, IPnpUtil pnp, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    private static DriverResult Fail(string message) => new(false, message);

    private static string Brief(CommandResult result)
    {
        var text = (result.Error.Length > 0 ? result.Error : result.Output).Trim().ReplaceLineEndings(" ");
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    /// <param name="say">Tells the person what is happening, one sentence at a time.</param>
    /// <param name="cancellationToken">Stops the install only before anything on the PC changes. Once the driver is being downloaded and installed, a stop is not honoured: ending a driver install half way can leave a device without a working driver.</param>
    public async Task<DriverResult> InstallAsync(string updateId, Action<string> say, CancellationToken cancellationToken)
    {
        if (!DriverGuard.IsUpdateId(updateId)) return Fail("That is not a Windows Update id.");
        try
        {
            say("Asking Windows Update what it offers…");
            IReadOnlyList<DriverOffer> offers;
            try { offers = await Task.Run(updates.Search, CancellationToken.None).ConfigureAwait(false); }
            catch (DriverUpdateException ex) { return Fail(ex.Message); }
            cancellationToken.ThrowIfCancellationRequested();
            var offer = offers.FirstOrDefault(x => string.Equals(x.UpdateId, updateId, StringComparison.OrdinalIgnoreCase));
            if (offer is null) return Fail("Windows Update does not offer that driver any more. Check for drivers again.");
            var finding = DriverCompare.Assess(offer, installed.Read());
            if (!finding.CanInstall) return Fail(finding.Reason);
            if (!offer.EulaAccepted) return Fail("This driver has licence terms that have to be accepted. Install it from Settings, Windows Update, Advanced options, Optional updates.");

            var current = finding.Installed;
            string? backupId = null;
            if (current is not null && DriverGuard.IsOemInf(current.InfName))
            {
                backupId = Guid.NewGuid().ToString("N");
                say($"Saving the current driver ({DriverCompare.DescribeInstalled(current)}) so it can be put back…");
                var saved = await SaveCurrentAsync(backupId, current, cancellationToken).ConfigureAwait(false);
                if (saved is not null) return Fail(saved);
            }
            else if (current is not null) say("The driver in use comes with Windows, which keeps it, so there is nothing to save.");
            // Last point at which a stop is honoured.
            cancellationToken.ThrowIfCancellationRequested();

            DriverInstallOutcome outcome;
            try { outcome = await Task.Run(() => updates.Install(updateId, say), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is DriverUpdateException or InvalidOperationException) { outcome = new(false, false, ex.Message); }
            if (!outcome.Ok)
            {
                if (backupId is not null) backups.Delete(backupId);
                return Fail(outcome.Error ?? "Windows Update did not install the driver.");
            }

            // Read the PC again: Windows Update saying "succeeded" is not the same as the device having the driver.
            var after = DriverCompare.Match(offer, installed.Read());
            var changed = after is not null && (current is null || !string.Equals(after.InfName, current.InfName, StringComparison.OrdinalIgnoreCase) || after.Version != current.Version || after.Date != current.Date);
            var toVersion = after?.Version is { Length: > 0 } v ? v : DriverCompare.VersionIn(offer.Title)?.ToString();
            if (backupId is not null)
            {
                if (changed && after is not null) TrySave(backupId, current!, after);
                else backups.Delete(backupId);
            }
            var restart = outcome.RebootRequired || offer.RebootRequired;
            if (!changed)
                return new(true, null, restart ? "Windows Update installed it, and Windows needs a restart before the device uses it." : "Windows Update says it installed, but this PC's records still show the old driver. It may take effect after a restart.", restart, null, current?.Version, toVersion, Verified: false);
            return new(true, null, restart ? "Installed. Windows needs a restart to finish." : "Installed.", restart, changed ? backupId : null, current?.Version, toVersion);
        }
        catch (OperationCanceledException) { return Fail("Stopped before anything was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return Fail(ex.Message); }
    }

    private async Task<string?> SaveCurrentAsync(string backupId, InstalledDriver current, CancellationToken cancellationToken)
    {
        try
        {
            backups.Save(new(backupId, current.DeviceKey, current.DeviceName, current.InfName, current.Version, current.Date, current.Provider, null, null, now()));
            var folder = backups.FolderFor(backupId);
            Directory.CreateDirectory(folder);
            var export = await pnp.ExportAsync(current.InfName, folder, cancellationToken).ConfigureAwait(false);
            if (export.ExitCode == 0 && Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Any()) return null;
            backups.Delete(backupId);
            return $"The current driver could not be saved, so nothing was changed. {Brief(export)}".TrimEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            backups.Delete(backupId);
            return $"The current driver could not be saved, so nothing was changed. {ex.Message}";
        }
    }

    /// <summary>Notes which driver the update added, so taking it out later removes that one and nothing else.</summary>
    private void TrySave(string backupId, InstalledDriver before, InstalledDriver after)
    {
        try
        {
            var record = backups.Load(backupId);
            if (record is null) return;
            backups.Save(record with { NewInfName = DriverGuard.IsOemInf(after.InfName) && !string.Equals(after.InfName, before.InfName, StringComparison.OrdinalIgnoreCase) ? after.InfName : null, NewVersion = after.Version });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Takes the driver an update added back out and lets the saved one serve the device again: the saved package goes back into Windows' driver store, then the
    /// update's package is removed, and Windows picks the best one left. Refused when the device has a different driver now than the one this installed.
    /// </summary>
    public async Task<DriverResult> RestoreAsync(string backupId, Action<string> say, CancellationToken cancellationToken)
    {
        if (!DriverGuard.IsBackupId(backupId)) return Fail("That is not a saved driver.");
        try
        {
            var record = backups.Load(backupId);
            if (record is null) return Fail("That saved driver is not available any more.");
            if (record.NewInfName is not { } added || !DriverGuard.IsOemInf(added))
                return Fail("TidyWindow cannot tell which driver the update added, so it cannot take it out. Use Device Manager, the device's Driver tab, Roll Back Driver.");
            var device = installed.Read().FirstOrDefault(x => string.Equals(x.DeviceKey, record.DeviceKey, StringComparison.OrdinalIgnoreCase));
            if (device is null) return Fail("That device is not on this PC any more.");
            if (!string.Equals(device.InfName, added, StringComparison.OrdinalIgnoreCase))
                return Fail("The driver for this device has changed again since TidyWindow installed that update, so putting the older one back could undo a later change. Use Device Manager instead.");
            var folder = backups.FolderFor(backupId);
            var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories) : [];
            if (files.Length == 0) return Fail("The saved copy of the driver is missing.");
            cancellationToken.ThrowIfCancellationRequested();

            say("Putting the saved driver back into Windows…");
            foreach (var file in files)
            {
                var add = await pnp.AddAsync(file, cancellationToken).ConfigureAwait(false);
                // 0 is done; 3010 is done, restart needed. Anything else leaves the PC as it was, since nothing has been removed yet.
                if (add.ExitCode is not (0 or 3010)) return Fail($"The saved driver could not be put back into Windows, so nothing was changed. {Brief(add)}".TrimEnd());
            }
            // From here the removal runs to the end: stopping between taking out the new driver and letting the old one serve could leave the device without either.
            say("Removing the driver the update added…");
            var remove = await pnp.DeleteAsync(added, CancellationToken.None).ConfigureAwait(false);
            var restart = remove.ExitCode == 3010;
            if (remove.ExitCode is not (0 or 3010)) return Fail($"Windows would not remove the update's driver. {Brief(remove)}".TrimEnd());
            await pnp.ScanAsync(CancellationToken.None).ConfigureAwait(false);

            var after = installed.Read().FirstOrDefault(x => string.Equals(x.DeviceKey, record.DeviceKey, StringComparison.OrdinalIgnoreCase));
            var back = after is not null && after.Version == record.Version;
            if (back) backups.Delete(backupId);
            return back
                ? new(true, null, restart ? "The earlier driver is back. Windows needs a restart to finish." : "The earlier driver is back.", restart, backupId, device.Version, after!.Version)
                : new(true, null, "The update's driver was removed, but this PC's records do not show the earlier driver yet. Restart Windows, then check Device Manager.", true, backupId, device.Version, after?.Version, Verified: false);
        }
        catch (OperationCanceledException) { return Fail("Stopped before anything was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return Fail(ex.Message); }
    }
}
