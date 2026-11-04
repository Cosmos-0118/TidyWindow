using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>
/// One request to the administrator helper. A request names what to change and, for a put-back, the <see cref="Token"/> of the helper's own copy of it;
/// it never carries the copy itself, so nothing that arrives over the pipe is ever written back with administrator rights.
/// </summary>
public sealed record RepairRequest(string Op, string? Path = null, string? Name = null, FootprintRequest? Footprint = null, string? Sid = null, string? File = null, MsiProduct? Product = null, string? Value = null, string? Token = null, string? Publisher = null);
public sealed record RepairReply(bool Ok, string? Error = null, KeyBackup? Backup = null, bool Flag = false, FootprintPlan? Plan = null, bool Denied = false, RunResult? Run = null, IReadOnlyList<ShortcutBackup>? Links = null, FolderBackup? Moved = null);

/// <summary>
/// The elevated side of repairs. It runs for one repair, asks Windows for permission once, and can only remove or restore the keys on
/// <see cref="RegistryRepair.Elevated"/>'s allow-list, repair a program from its own installer file, put a signed setup program back in (or take it out of) the package cache, remove or restore shortcut files and empty folders in the all-users Start menu and Public Desktop, set aside, put back or delete the installer caches and program folders of the allow-list in <see cref="RepairFolders"/>,
/// or stop the Windows Installer service,
/// so it cannot run anything else or touch any other setting. It leaves by itself when idle.
/// Whatever it removes it copies into its own protected store first (<see cref="RepairVault"/>) and hands the app a token for the copy; a put-back names the token,
/// and the helper puts back its own copy. The app's copy, kept in files any program running as the ordinary account could edit, is for the Undo tab only.
/// </summary>
public static class RepairHost
{
    public const string Command = "--repair";
    private const string NoCopy = "There is no protected copy of this to put back. It may have been removed by an earlier version of TidyWindow, or its copy may have been cleared.";
    private const string NotThis = "That copy does not belong to what was asked for.";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <param name="opsFor">The installer operations for the account the helper was started for (its SID), so the package cache in that account's profile is the only one reached.</param>
    /// <param name="vault">Where the helper keeps its copies. The protected store under ProgramData unless a test supplies its own.</param>
    public static async Task<int> RunAsync(string[] args, Func<string, IKeyEditor>? editorFor = null, Func<string, IElevatedOps>? opsFor = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null, IShortcutEditor? shortcuts = null, IFolderEditor? folders = null, RepairVault? vault = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (!OperatingSystem.IsWindows() && (editorFor is null || opsFor is null || vault is null)) return 66;
        vault ??= new RepairVault(new ProtectedStore("repair"));
        if (OperatingSystem.IsWindows())
        {
            editorFor ??= sid => { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); RegistryNative.EnablePrivileges(); return RegistryRepair.Elevated(sid); };
            opsFor ??= sid => { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return new WindowsInstallerOps(vault, WindowsInstallerOps.CacheRootsFor(sid), elevated: true); };
            shortcuts ??= FileShortcutEditor.Machine();
            folders ??= FileFolderEditor.Machine(vault.Quarantine);
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = await HelperPipe.TryConnectAsync(args[1]).ConfigureAwait(false);
        if (pipe is null) return 65;
        var reader = new PipeLineReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };

        async Task Reply(RepairReply reply)
        {
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json)).ConfigureAwait(false); }
            catch (IOException) { }
        }

        IKeyEditor? editor = null;
        IElevatedOps? ops = null;
        while (true)
        {
            string? line;
            using (var timeout = new CancellationTokenSource(idle ?? TimeSpan.FromMinutes(5)))
            {
                try { line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { return 0; }
            }
            if (line is null) return 0;
            RepairRequest? request;
            try { request = JsonSerializer.Deserialize<RepairRequest>(line, Json); }
            catch (JsonException) { request = null; }
            if (request is null) { await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); continue; }

            if (request.Op == "bye") return 0;
            if (request.Op == "hello")
            {
                var admin = isAdministrator();
                // The account whose hive HKCU means is the one that made the pipe, read from the pipe itself; a request may only repeat it, never name another.
                var owner = OperatingSystem.IsWindows() ? OwnerOf(pipe) : null;
                var sid = owner ?? request.Sid ?? (OperatingSystem.IsWindows() ? RegistryRepair.CurrentSid() : "");
                if (owner is not null && request.Sid is { Length: > 0 } asked && !string.Equals(asked, owner, StringComparison.OrdinalIgnoreCase))
                { await Reply(new(false, "The helper was started for another account.")).ConfigureAwait(false); continue; }
                if (admin)
                {
                    // The protected folder is made (with its administrator-only permissions) before anything is kept in it, and old copies are cleared out.
                    try { vault.Ready(); vault.Prune(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Reply(new(false, "The helper could not make its protected folder: " + ex.Message)).ConfigureAwait(false); continue; }
                    editor = editorFor!(sid);
                    ops = opsFor!(sid);
                }
                await Reply(new(admin)).ConfigureAwait(false);
                continue;
            }
            if (editor is null || ops is null) { await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); continue; }
            await Reply(Handle(request, editor, ops, shortcuts, folders, vault)).ConfigureAwait(false);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? OwnerOf(PipeStream pipe)
    {
        try { return (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private static RepairReply Handle(RepairRequest request, IKeyEditor editor, IElevatedOps ops, IShortcutEditor? shortcuts, IFolderEditor? folders, RepairVault vault)
    {
        const string Refused = "TidyWindow does not change that part of the registry.";
        try
        {
            switch (request.Op)
            {
                case "exists" when Allowed(editor, request.Path): return new(true, Flag: editor.Exists(request.Path!));
                case "snapshot" when Allowed(editor, request.Path): return Kept(vault, editor.Snapshot(request.Path!));
                case "delete" when Allowed(editor, request.Path): return KeptOrUndone(vault, editor, editor.Delete(request.Path!));
                case "deletevalue" when Allowed(editor, request.Path) && !string.IsNullOrEmpty(request.Name): return KeptOrUndone(vault, editor, editor.DeleteValue(request.Path!, request.Name));
                case "settext" when Allowed(editor, request.Path) && !string.IsNullOrEmpty(request.Name) && request.Value is not null: return KeptOrUndone(vault, editor, editor.SetText(request.Path!, request.Name, request.Value));
                case "restore" when request.Path is { Length: > 0 } path:
                {
                    // Only the helper's own copy is written back, and only to the key it was taken from; nothing in the request but the token and the path is looked at.
                    if (vault.Take<KeyBackup>(request.Token) is not { } copy) return new(false, NoCopy);
                    if (!string.Equals(copy.Path, path, StringComparison.OrdinalIgnoreCase) || !Allowed(editor, copy.Path)) return new(false, NotThis);
                    editor.Restore(copy);
                    vault.Forget(request.Token);
                    return new(true);
                }
                case "footprint" when request.Footprint is { } footprint: return new(true, Plan: editor.Footprint(footprint));
                case "reinstall" when request.File is { Length: > 0 } file && request.Product is { } product: return new(true, Run: ops.Reinstall(file, product));
                case "stopservice": return new(true, Flag: ops.StopInstallerService());
                case "restorebundle" when request.File is { Length: > 0 } setup && request.Name is { Length: > 0 } program && request.Path is { Length: > 0 } target: return new(true, Run: ops.RestoreBundleCopy(setup, program, request.Publisher ?? "", target));
                case "removecopy" when request.Path is { Length: > 0 } copy: return new(true, Flag: ops.RemoveCopy(copy));
                // Where the file goes and whether it needs a signature are the helper's to decide; only the candidate's path and the update it claims to be come from the request.
                case "restorepatch" when request.File is { Length: > 0 } update && request.Name is { Length: > 0 } code && request.Path is { Length: > 0 } saved: return new(true, Run: ops.RestorePatchCopy(update, code, saved));
                case "rmlink" when shortcuts is not null && request.Path is { Length: > 0 } link: return KeptOrUndone(vault, shortcuts, shortcuts.RemoveShortcut(link));
                case "restorelink" when shortcuts is not null && request.Path is { Length: > 0 } path:
                {
                    if (vault.Take<ShortcutBackup>(request.Token) is not { } copy) return new(false, NoCopy);
                    if (!string.Equals(copy.Path, path, StringComparison.OrdinalIgnoreCase)) return new(false, NotThis);
                    // The copy is the helper's own, but what it writes is checked once more: a shortcut file of a sensible size, or a folder, nothing else.
                    if (copy.Data is not null && (!IsShortcutFile(copy.Path) || copy.Data.Length > MaxShortcutBase64)) return new(false, "Only a shortcut file is ever put back here.");
                    shortcuts.RestoreShortcut(copy);
                    vault.Forget(request.Token);
                    return new(true);
                }
                case "setaside" when folders is not null && request.Path is { Length: > 0 } aside:
                {
                    // The folder goes into the helper's own quarantine, under a token, whatever folder the request named.
                    var token = RepairVault.NewToken();
                    var moved = folders.SetAside(aside, Path.Combine(vault.Quarantine, token));
                    if (moved is null) return new(true);
                    try { vault.Keep(token, moved); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        folders.PutBack(moved);
                        return new(false, "The record needed to put this folder back could not be kept, so it was not set aside. " + ex.Message);
                    }
                    return new(true, Moved: moved with { Token = token });
                }
                case "putback" or "takeagain" or "discard" when folders is not null && request.Path is { Length: > 0 } original:
                {
                    var kept = vault.Take<FolderBackup>(request.Token);
                    // A folder that is gone for good is what a discard asks for; a put-back of something the helper never set aside is not.
                    if (kept is null) return request.Op == "discard" ? new(true) : new(false, NoCopy);
                    if (!string.Equals(kept.Original, original, StringComparison.OrdinalIgnoreCase)) return new(false, NotThis);
                    switch (request.Op)
                    {
                        case "putback": folders.PutBack(kept); break;
                        case "takeagain": folders.TakeAgain(kept); break;
                        default: folders.Discard(kept); vault.Forget(request.Token); break;
                    }
                    return new(true);
                }
                case "exists" or "snapshot" or "delete" or "deletevalue" or "settext" or "restore": return new(false, Refused);
                default: return new(false, "That request was not understood.");
            }
        }
        catch (UnauthorizedAccessException ex) { return new(false, ex.Message, Denied: true); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException or ArgumentException or FormatException) { return new(false, ex.Message); }
    }

    /// <summary>A shortcut file is a few kilobytes; a copy far larger than any real one is not written back, whatever it is.</summary>
    private const int MaxShortcutBase64 = 4 * 1024 * 1024;

    private static bool IsShortcutFile(string path) => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    private static RepairReply Kept(RepairVault vault, KeyBackup? copy) => copy is null ? new(true) : new(true, Backup: copy with { Token = vault.Keep(copy) });

    /// <summary>The copy of a change is kept before the change counts as made: when it cannot be, the change is taken back and refused.</summary>
    private static RepairReply KeptOrUndone(RepairVault vault, IKeyEditor editor, KeyBackup? copy)
    {
        if (copy is null) return new(true);
        try { return Kept(vault, copy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string back;
            try { editor.Restore(copy); back = "so the change was taken back."; }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { back = "and the change could not be taken back: " + again.Message; }
            return new(false, $"The copy needed to undo this change could not be kept ({ex.Message}), {back}");
        }
    }

    private static RepairReply KeptOrUndone(RepairVault vault, IShortcutEditor shortcuts, IReadOnlyList<ShortcutBackup> copies)
    {
        var kept = new List<ShortcutBackup>(copies.Count);
        try
        {
            foreach (var copy in copies) kept.Add(copy with { Token = vault.Keep(copy) });
            return new(true, Links: kept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What was removed goes back the way it came out: the folder before the file in it.
            foreach (var copy in Enumerable.Reverse(copies))
            {
                try { shortcuts.RestoreShortcut(copy); }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { }
            }
            foreach (var copy in kept) vault.Forget(copy.Token);
            return new(false, "The copy needed to put this back could not be kept, so it was not removed. " + ex.Message);
        }
    }

    private static bool Allowed(IKeyEditor editor, string? path) => !string.IsNullOrWhiteSpace(path) && editor.IsAllowed(path);
}

/// <summary>The app side: starts the helper through the Windows permission prompt and sends it each change over a private pipe.</summary>
public sealed class RepairSession(Func<string, Task> launch) : IKeyEditor, IElevatedOps, IShortcutEditor, IFolderEditor, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private PipeLineReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static RepairSession Elevated(string helperPath) => new(name => HelperPipe.LaunchElevated(helperPath, RepairHost.Command, name));

    /// <summary>Asks for permission and connects. Throws <see cref="OperationCanceledException"/> if the person says no, <see cref="IOException"/> if the helper could not be started.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen) return;
        Close();
        var name = HelperPipe.NewName("repair");
        var server = HelperPipe.CreateServer(name);
        try
        {
            var launched = Task.Run(() => launch(name), CancellationToken.None);
            var connecting = server.WaitForConnectionAsync(cancellationToken);
            if (await Task.WhenAny(connecting, launched).ConfigureAwait(false) != connecting)
            {
                await launched.ConfigureAwait(false);
                throw new IOException("The administrator helper stopped before it could connect.");
            }
            await connecting.ConfigureAwait(false);
            var input = new PipeLineReader(server);
            var output = new StreamWriter(server) { AutoFlush = true };
            await output.WriteLineAsync(JsonSerializer.Serialize(new RepairRequest("hello", Sid: OperatingSystem.IsWindows() ? RegistryRepair.CurrentSid() : null), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<RepairReply>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException(reply?.Error ?? "The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>Removes a key and returns a copy of it, or null if it was already gone. The copy carries the token of the helper's own protected copy.</summary>
    public KeyBackup? Delete(string path) => Send(new("delete", path)).Backup;

    public KeyBackup? DeleteValue(string path, string name) => Send(new("deletevalue", path, Name: name)).Backup;

    public KeyBackup? SetText(string path, string name, string value) => Send(new("settext", path, Name: name, Value: value)).Backup;

    /// <summary>Has the helper put back its own protected copy of the key, named by the copy's token. The values in <paramref name="backup"/> itself are never sent.</summary>
    public void Restore(KeyBackup backup) => Send(new("restore", backup.Path, Token: backup.Token));

    public bool Exists(string path) => Send(new("exists", path)).Flag;

    public KeyBackup? Snapshot(string path) => Send(new("snapshot", path)).Backup;

    public FootprintPlan Footprint(FootprintRequest request) => Send(new("footprint", Footprint: request)).Plan ?? FootprintPlan.Empty;

    public RunResult Reinstall(string msiPath, MsiProduct product, CancellationToken cancellationToken = default) => Send(new("reinstall", File: msiPath, Product: product)).Run ?? new(1, "The helper gave no answer.");

    public bool StopInstallerService() => Send(new("stopservice")).Flag;

    public RunResult RestoreBundleCopy(string exePath, string programName, string publisher, string targetPath) => Send(new("restorebundle", targetPath, Name: programName, File: exePath, Publisher: publisher)).Run ?? new(1, "The helper gave no answer.");

    public bool RemoveCopy(string path) => Send(new("removecopy", path)).Flag;

    public RunResult RestorePatchCopy(string mspPath, string patchCode, string targetPath) =>
        Send(new("restorepatch", targetPath, Name: patchCode, File: mspPath)).Run ?? new(1, "The helper gave no answer.");

    public IReadOnlyList<ShortcutBackup> RemoveShortcut(string path) => Send(new("rmlink", path)).Links ?? [];

    public void RestoreShortcut(ShortcutBackup backup) => Send(new("restorelink", backup.Path, Token: backup.Token));

    /// <summary>The helper keeps the folder in its own quarantine; <paramref name="vault"/> is the app's and is not sent.</summary>
    public FolderBackup? SetAside(string path, string vault) => Send(new("setaside", path)).Moved;

    public void PutBack(FolderBackup backup) => Send(new("putback", backup.Original, Token: backup.Token));

    public void TakeAgain(FolderBackup backup) => Send(new("takeagain", backup.Original, Token: backup.Token));

    public void Discard(FolderBackup backup) => Send(new("discard", backup.Original, Token: backup.Token));

    private RepairReply Send(RepairRequest request)
    {
        lock (gate)
        {
            if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("The administrator helper is not running.");
            RepairReply? reply;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(request, Json));
                reply = JsonSerializer.Deserialize<RepairReply>(reader.ReadLine() ?? "", Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
            {
                CloseCore();
                throw new IOException("The administrator helper stopped. Try again.");
            }
            if (reply is null) { CloseCore(); throw new IOException("The administrator helper stopped. Try again."); }
            if (!reply.Ok) throw reply.Denied ? new UnauthorizedAccessException(reply.Error) : new IOException(reply.Error ?? "The change could not be made.");
            return reply;
        }
    }

    public void Close() { lock (gate) CloseCore(); }

    private void CloseCore()
    {
        try { writer?.WriteLine(JsonSerializer.Serialize(new RepairRequest("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() => Close();
}
