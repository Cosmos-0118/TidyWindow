using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

public sealed record PathRequest(string Op, string? Expected = null, string? Value = null);
public sealed record PathReply(bool Ok, bool Stale = false, string? Error = null);

/// <summary>
/// The elevated side of Windows-wide PATH editing. TidyWindow itself never runs as administrator, so this runs as a separate process that stays
/// for as long as the person keeps editing unlocked. It can do one thing, replace the Windows-wide PATH, and only when that PATH is still the
/// one the caller based the change on, so it can neither run anything nor touch any other setting. It leaves by itself after sitting idle.
/// </summary>
public static class MachinePathHost
{
    public const string Command = "--machine-path";
    private const int MaxLength = 32000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <param name="check">Judges a new PATH against the current one before it is written; see <see cref="MachinePathGuard"/>. Replaceable in tests.</param>
    public static async Task<int> RunAsync(string[] args, Func<string?>? read = null, Action<string>? write = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null, Func<string?, string, string?>? check = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        read ??= () => OperatingSystem.IsWindows() ? new RegistryPathStore().Read(PathScope.Machine) : null;
        write ??= value => { if (OperatingSystem.IsWindows()) new RegistryPathStore().WriteMachine(value); };
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        check ??= MachinePathGuard.Problem;
        using var pipe = await HelperPipe.TryConnectAsync(args[1]).ConfigureAwait(false);
        if (pipe is null) return 65;
        var reader = new PipeLineReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };

        async Task Reply(PathReply reply)
        {
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json)).ConfigureAwait(false); }
            catch (IOException) { }
        }

        while (true)
        {
            string? line;
            using (var timeout = new CancellationTokenSource(idle ?? TimeSpan.FromMinutes(10)))
            {
                try { line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { return 0; }
            }
            if (line is null) return 0;
            PathRequest? request;
            try { request = JsonSerializer.Deserialize<PathRequest>(line, Json); }
            catch (JsonException) { request = null; }
            if (request is null) { await Reply(new(false, Error: "That request was not understood.")).ConfigureAwait(false); continue; }

            switch (request.Op)
            {
                case "bye": return 0;
                case "hello": await Reply(new(isAdministrator())).ConfigureAwait(false); break;
                case "write": await Reply(Write(request, read, write, check)).ConfigureAwait(false); break;
                default: await Reply(new(false, Error: "That request was not understood.")).ConfigureAwait(false); break;
            }
        }
    }

    private static PathReply Write(PathRequest request, Func<string?> read, Action<string> write, Func<string?, string, string?> check)
    {
        var value = request.Value ?? "";
        // A damaged PATH breaks every program on the PC, so anything that is empty, enormous or not plain text is refused here whatever asked for it.
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength || value.Any(char.IsControl)) return new(false, Error: "That PATH is not valid, so nothing was changed.");
        var current = read() ?? "";
        if (!string.Equals(current, request.Expected ?? "", StringComparison.Ordinal)) return new(false, Stale: true);
        // The helper judges additions itself: the request came over a pipe from a process without administrator rights.
        if (check(current, value) is { } problem) return new(false, Error: problem);
        try { write(value); return new(true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException) { return new(false, Error: ex.Message); }
    }
}

/// <summary>
/// What the helper checks before it writes a new Windows-wide PATH. The list decides which program runs when any account, including an
/// administrator, types a name, so a folder that an ordinary account can change must not be added to it: a program planted there would run with
/// administrator rights later. Folders already on the list are left alone (they are the PC's own doing); the rules apply to additions.
/// </summary>
public static class MachinePathGuard
{
    private const int MaxEntryLength = 1024;

    /// <summary>The reason a new value must be refused, or null when every added folder is acceptable.</summary>
    public static string? Problem(string? current, string value) =>
        Problem(current, value, Environment.ExpandEnvironmentVariables, Directory.Exists, folder => OperatingSystem.IsWindows() && IsOpenToOrdinaryAccounts(folder));

    /// <param name="openToOrdinaryAccounts">Whether an account without administrator rights can change the folder or any folder above it.</param>
    public static string? Problem(string? current, string value, Func<string, string> expand, Func<string, bool> directoryExists, Func<string, bool> openToOrdinaryAccounts)
    {
        var before = PathEdit.Items(current).Select(item => PathAnalyzer.Key(expand(item.Trim('"')))).ToHashSet(StringComparer.Ordinal);
        foreach (var item in PathEdit.Items(value))
        {
            if (item.Length > MaxEntryLength) return "One entry is longer than a folder path can be, so nothing was changed.";
            var folder = expand(item.Trim('"'));
            if (before.Contains(PathAnalyzer.Key(folder))) continue;
            var shown = Shown(item);
            if (folder.Contains('%')) return $"{shown} uses a variable this PC does not have, so it was not added.";
            if (folder.StartsWith(@"\\", StringComparison.Ordinal)) return $"{shown} is a network location; the Windows-wide list only takes folders on this PC.";
            if (!Path.IsPathFullyQualified(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return $"{shown} is not a full folder path, so it was not added.";
            if (!directoryExists(folder)) return $"{shown} does not exist, so it was not added.";
            if (openToOrdinaryAccounts(folder)) return $"{shown} can be changed by accounts without administrator rights, so it cannot go on the Windows-wide list. Add it to your own PATH instead.";
        }
        return null;
    }

    /// <summary>
    /// True when an account without administrator rights can change the folder, or rename or replace it through a folder above it. Everything
    /// under the profiles folder counts, whatever its permissions say today, because every account owns its own tree there.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOpenToOrdinaryAccounts(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return true; }
        if (IsUnder(full, ProfilesFolder())) return true;
        var target = true;
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent, target = false)
        {
            DirectorySecurity security;
            try { security = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { return true; }
            if (Opens(security, target)) return true;
        }
        return false;
    }

    /// <summary>Whether these permissions let an ordinary account change the folder (<paramref name="target"/>) or take away a folder inside it.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool Opens(DirectorySecurity security, bool target)
    {
        // On the folder itself, planting or renaming anything is enough. On a folder above it, only taking the folder away matters: adding a
        // sibling cannot change what the PATH entry names.
        const FileSystemRights Above = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        const FileSystemRights Within = Above | FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete;
        var rights = target ? Within : Above;
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && IsOrdinary(owner)) return true;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((rule.FileSystemRights & rights) == 0) continue;
            if (IsOrdinary((SecurityIdentifier)rule.IdentityReference)) return true;
        }
        return false;
    }

    // Groups every signed-in account belongs to, app containers, and individual accounts. A program planted by any of them runs for everyone who uses the PATH.
    [SupportedOSPlatform("windows")]
    private static bool IsOrdinary(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.WorldSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) ||
        sid.IsWellKnown(WellKnownSidType.InteractiveSid) || sid.IsWellKnown(WellKnownSidType.BuiltinGuestsSid) || sid.IsWellKnown(WellKnownSidType.AnonymousSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinPowerUsersSid) || sid.IsWellKnown(WellKnownSidType.NetworkSid) ||
        sid.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal) || sid.IsAccountSid();

    [SupportedOSPlatform("windows")]
    private static string ProfilesFolder()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (key?.GetValue("ProfilesDirectory") is string value && value.Length > 0) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
    }

    private static bool IsUnder(string path, string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(trimmed, StringComparison.OrdinalIgnoreCase) || path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Shown(string item) => item.Length <= 80 ? "\"" + item + "\"" : "\"" + item[..77] + "…\"";
}

/// <summary>The app side: starts the elevated helper once, then sends it each edit over a private pipe until editing is locked again.</summary>
public sealed class MachinePathSession(Func<string, Task> launch) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private PipeLineReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    /// <summary>Starts the helper through the Windows permission prompt.</summary>
    public static MachinePathSession Elevated(string helperPath) => new(name => HelperPipe.LaunchElevated(helperPath, MachinePathHost.Command, name));

    /// <summary>Asks for permission and connects. Throws <see cref="OperationCanceledException"/> if the person says no.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen) return;
        Close();
        var name = HelperPipe.NewName("path");
        var server = HelperPipe.CreateServer(name);
        try
        {
            // Starting an elevated process waits for the permission prompt, so it never runs on the caller's thread and has no timer of its own.
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new PathRequest("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<PathReply>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>Replaces the Windows-wide PATH if it still equals <paramref name="expected"/>.</summary>
    public void Write(string? expected, string value)
    {
        lock (gate)
        {
            if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("Editing the Windows-wide PATH is locked.");
            PathReply? reply;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(new PathRequest("write", expected ?? "", value), Json));
                reply = JsonSerializer.Deserialize<PathReply>(reader.ReadLine() ?? "", Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
            {
                CloseCore();
                throw new IOException("The administrator helper stopped. Unlock editing again to continue.");
            }
            if (reply is null) { CloseCore(); throw new IOException("The administrator helper stopped. Unlock editing again to continue."); }
            if (reply.Stale) throw new IOException(PathDoctor.Stale);
            if (!reply.Ok) throw new IOException(reply.Error ?? "The Windows-wide PATH could not be saved.");
        }
    }

    public void Close() { lock (gate) CloseCore(); }

    private void CloseCore()
    {
        try { writer?.WriteLine(JsonSerializer.Serialize(new PathRequest("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() => Close();
}

/// <summary>Reads PATH the ordinary way and sends writes of the Windows-wide list through the elevated helper.</summary>
public sealed class ElevatedPathStore(IPathStore inner, MachinePathSession session) : IPathStore
{
    public string? Read(PathScope scope) => inner.Read(scope);
    public string? Variable(PathScope scope, string name) => inner.Variable(scope, name);
    public void WriteVariable(string name, string? value) => inner.WriteVariable(name, value);

    public void Write(PathScope scope, string value, string? previous)
    {
        if (scope == PathScope.Machine) session.Write(previous, value);
        else inner.Write(scope, value, previous);
    }
}
