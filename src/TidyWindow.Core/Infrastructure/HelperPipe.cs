using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TidyWindow.Next.Core;

/// <summary>
/// The private pipe between TidyWindow and its administrator helper, and the way the helper is started. Every elevated feature uses the
/// same arrangement: the app makes a pipe with a random name that only this account (and administrators, who approve the prompt) can open,
/// starts the helper through the Windows permission prompt, and the helper connects back by name and checks that the pipe belongs to the
/// account it was started for. Messages are JSON lines; a line longer than <see cref="MaxLineLength"/> ends the conversation instead of
/// being buffered.
/// </summary>
public static class HelperPipe
{
    /// <summary>The most characters one message may have. Startup items carry a shortcut (a few MiB of base64 at most) or a task definition, nothing larger.</summary>
    public const int MaxLineLength = 16 * 1024 * 1024;

    /// <summary>How long the helper waits for the app's pipe. The app creates the pipe before it starts the helper, so this only covers a slow start.</summary>
    public const int ConnectTimeout = 15000;

    /// <summary>A pipe name nobody can guess ahead of time.</summary>
    public static string NewName(string area) => "tidywindow-" + area + "-" + Guid.NewGuid().ToString("N");

    /// <summary>The app side: one pipe for one helper, in this account's name, that no other ordinary account can open.</summary>
    public static NamedPipeServerStream CreateServer(string name)
    {
        if (OperatingSystem.IsWindows()) return CreateWindowsServer(name);
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current account has no security identifier.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // The helper runs as whoever approved the prompt: this account, or another administrator when this account is not one.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(user);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>The helper side: connects to the app's pipe and refuses one made by another account.</summary>
    /// <exception cref="TimeoutException">The pipe did not accept the connection in time.</exception>
    /// <exception cref="IOException">The pipe is gone or broken.</exception>
    /// <exception cref="UnauthorizedAccessException">The pipe belongs to another account.</exception>
    public static async Task<NamedPipeClientStream> ConnectAsync(string name, int timeout = ConnectTimeout, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsWindows()) CheckOwner(pipe);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>Like <see cref="ConnectAsync"/>, but answers null instead of throwing when the pipe cannot be used; a helper then simply exits.</summary>
    public static async Task<NamedPipeClientStream?> TryConnectAsync(string name, int timeout = ConnectTimeout)
    {
        try { return await ConnectAsync(name, timeout).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static void CheckOwner(NamedPipeClientStream pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is not null && identity.User is { } user && owner.Equals(user)) return;
        // An administrator who approved the prompt for another account's TidyWindow is talking to that account's pipe. The helper is bound
        // by the same catalog rules whoever is on the other end, so the only thing refused is a pipe that no account made.
        if (owner is not null && owner.IsAccountSid() && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        throw new UnauthorizedAccessException("The pipe does not belong to the account that started the helper.");
    }

    /// <summary>Starts the helper through the Windows permission prompt. The returned task completes when the helper exits.</summary>
    /// <exception cref="OperationCanceledException">The person declined the prompt.</exception>
    /// <exception cref="IOException">The helper is missing from this installation, or Windows would not start it (a policy that blocks it, a damaged file).</exception>
    public static Task LaunchElevated(string helperPath, string command, params string[] arguments)
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The administrator helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helperPath) ?? ""
        };
        info.ArgumentList.Add(command);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator permission was declined."); }
        catch (Win32Exception ex) { throw new IOException("The administrator helper could not be started: " + ex.Message, ex); }
        if (process is null) throw new IOException("The administrator helper could not be started.");
        return process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
    }

    /// <summary>True when a launch task ended because the person declined the permission prompt, however the launcher reported it.</summary>
    public static bool WasDeclined(Task launched) =>
        launched.IsCanceled || (launched.IsFaulted && launched.Exception?.GetBaseException() is OperationCanceledException);
}

/// <summary>
/// Reads one JSON line at a time from a pipe, with a ceiling on how long a line may be. A reader without a ceiling would hold whatever the
/// other side sends, so a damaged or hostile message could make the helper or the app keep hundreds of megabytes in memory.
/// </summary>
public sealed class PipeLineReader(Stream stream, int maxLength = HelperPipe.MaxLineLength)
{
    private readonly byte[] bytes = new byte[8192];
    private readonly char[] chars = new char[Encoding.UTF8.GetMaxCharCount(8192)];
    private readonly Decoder decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder line = new();
    private int start, count;

    /// <returns>The next line without its line ending, or null once the other side has closed the pipe.</returns>
    /// <exception cref="IOException">The pipe broke, or a line went past the ceiling.</exception>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (TakeLine(out var done)) return done;
            var read = await stream.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return Flush();
            Decode(read);
        }
    }

    /// <returns>The next line without its line ending, or null once the other side has closed the pipe.</returns>
    /// <exception cref="IOException">The pipe broke, or a line went past the ceiling.</exception>
    public string? ReadLine()
    {
        while (true)
        {
            if (TakeLine(out var done)) return done;
            var read = stream.Read(bytes, 0, bytes.Length);
            if (read == 0) return Flush();
            Decode(read);
        }
    }

    private void Decode(int read)
    {
        count = decoder.GetChars(bytes, 0, read, chars, 0);
        start = 0;
    }

    private bool TakeLine(out string? result)
    {
        while (start < count)
        {
            var c = chars[start++];
            if (c == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                result = line.ToString();
                line.Clear();
                return true;
            }
            line.Append(c);
            if (line.Length > maxLength)
            {
                line.Clear();
                throw new IOException("A message on the pipe was too long to be read.");
            }
        }
        result = null;
        return false;
    }

    private string? Flush()
    {
        if (line.Length == 0) return null;
        var text = line.ToString();
        line.Clear();
        return text;
    }
}
