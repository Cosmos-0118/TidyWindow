using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>One line the font helper sends back: "ready" once it has permission, then "done" or "failed".</summary>
public sealed record FontMessage(string Kind, string? Message = null, string? Detail = null, int? Percent = null);

/// <summary>What the font install is doing now. <paramref name="Percent"/> is how far along it is overall (0 to 100), or null when that cannot be said.</summary>
public sealed record FontProgress(string Step, int? Percent);

/// <summary>Where machine-wide fonts live. Replaceable so the install logic can be tested without administrator rights.</summary>
public interface IFontStore
{
    /// <summary>Whether the Fonts folder already holds exactly this file.</summary>
    bool Matches(string file, byte[] content);
    void Write(string file, byte[] content);
    /// <summary>Lists the file under <paramref name="registration"/> for every account and makes it available to running programs.</summary>
    void Register(string registration, string file);
    /// <summary>Takes the registration and the file away. Throws <see cref="IOException"/> when the file is still in use.</summary>
    void Remove(string registration, string file);
    /// <summary>Tells running programs that the set of fonts changed.</summary>
    void Announce();
}

/// <summary>
/// Installs the terminal font for every account, which is what Windows Terminal needs: a font installed for one account only is often not seen by
/// it, or is gone after a restart. It downloads one fixed archive, refuses it unless its checksum matches, and writes only the four faces named in
/// <see cref="TerminalKit.FontFiles"/> under file names it chose itself, so nothing in the archive decides where anything is written.
/// </summary>
/// <param name="download">Fetches the package, calling its first argument with the bytes read so far and the total when it is known.</param>
public sealed class FontInstaller(Func<Action<long, long?>, CancellationToken, Task<byte[]>> download, IFontStore store, string? expectedSha256 = null)
{
    private const int MaxFaceBytes = 16 * 1024 * 1024;
    private readonly string expected = expectedSha256 ?? TerminalKit.FontSha256;

    public async Task<TerminalResult> InstallAsync(CancellationToken token, IProgress<FontProgress>? progress = null)
    {
        // The download is nine tenths of the work, so it fills most of the bar; checking and installing share the rest.
        var shown = -1;
        void Downloading(long read, long? total)
        {
            var size = total is > 0 ? total.Value : TerminalKit.FontApproxBytes;
            var percent = (int)Math.Min(90, read * 90 / Math.Max(1, size));
            if (percent == shown) return;
            shown = percent;
            progress?.Report(new($"Downloading the font… {read / 1048576.0:0} of {size / 1048576.0:0} MB", percent));
        }
        progress?.Report(new("Downloading the font…", 0));
        byte[] archive;
        try { archive = await download(Downloading, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(false, "Installing the font was stopped."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
        { return new(false, "The font could not be downloaded. Check your connection and try again.", ex.Message); }

        progress?.Report(new("Checking the download…", 92));
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(archive)), expected, StringComparison.OrdinalIgnoreCase))
            return new(false, "The downloaded font did not match the expected checksum, so nothing was installed.", "Expected SHA-256 " + expected);

        var faces = new List<(FontFile Face, byte[] Content)>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            foreach (var face in TerminalKit.FontFiles)
            {
                var entry = zip.GetEntry(face.File);
                if (entry is null || entry.Length is <= 0 or > MaxFaceBytes) return new(false, "The font package did not contain what was expected, so nothing was installed.", face.File);
                using var input = entry.Open();
                using var buffer = new MemoryStream((int)entry.Length);
                input.CopyTo(buffer);
                faces.Add((face, buffer.ToArray()));
            }
        }
        catch (InvalidDataException ex) { return new(false, "The font package could not be opened, so nothing was installed.", ex.Message); }

        var added = new List<FontFile>();
        var done = 0;
        try
        {
            foreach (var (face, content) in faces)
            {
                progress?.Report(new("Installing for everyone on this PC…", 94 + done++ * 5 / faces.Count));
                if (!store.Matches(face.File, content)) { store.Write(face.File, content); added.Add(face); }
                store.Register(face.Registration, face.File);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Half a font family is worse than none, so what this run put there goes again; files that were already there stay.
            foreach (var face in added) { try { store.Remove(face.Registration, face.File); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or System.Security.SecurityException) { } }
            return new(false, "The font could not be installed. If a program is using an older copy, close it and try again.", ex.Message);
        }
        store.Announce();
        progress?.Report(new("Done", 100));
        return new(true, $"{TerminalKit.FontFace} is installed for everyone on this PC. Open a new terminal window to use it.");
    }

    public TerminalResult Remove(IProgress<FontProgress>? progress = null)
    {
        progress?.Report(new("Removing the font…", null));
        var stuck = new List<string>();
        foreach (var face in TerminalKit.FontFiles)
        {
            try { store.Remove(face.Registration, face.File); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { stuck.Add($"{face.File}: {ex.Message}"); }
        }
        store.Announce();
        return stuck.Count == 0
            ? new(true, $"{TerminalKit.FontFace} was removed.")
            : new(false, "Some font files are still in use. Close Windows Terminal and other programs that show it, then try again.", string.Join("\n", stuck));
    }
}

/// <summary>The real Fonts folder and registry. Needs administrator rights.</summary>
public sealed class WindowsFontStore : IFontStore
{
    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private static string Folder => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public bool Matches(string file, byte[] content)
    {
        var path = Path.Combine(Folder, file);
        try { return File.Exists(path) && new FileInfo(path).Length == content.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(content); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Write(string file, byte[] content)
    {
        var path = Path.Combine(Folder, file);
        var temp = path + ".tidywindow-new";
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    public void Register(string registration, string file)
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var key = Registry.LocalMachine.CreateSubKey(RegistryKey)) key.SetValue(registration, file, RegistryValueKind.String);
        AddFontResourceW(Path.Combine(Folder, file));
    }

    public void Remove(string registration, string file)
    {
        var path = Path.Combine(Folder, file);
        if (OperatingSystem.IsWindows())
        {
            RemoveFontResourceW(path);
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey, writable: true);
            key?.DeleteValue(registration, throwOnMissingValue: false);
        }
        File.Delete(path);
    }

    public void Announce()
    {
        if (!OperatingSystem.IsWindows()) return;
        // HWND_BROADCAST, WM_FONTCHANGE, SMTO_ABORTIFHUNG: programs that are not responding are skipped rather than waited for.
        try { SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001D, UIntPtr.Zero, IntPtr.Zero, 0x0002, 3000, out _); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResourceW(string path);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool RemoveFontResourceW(string path);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}

/// <summary>
/// The elevated side of the font install. It takes one word over the pipe, install or remove, and nothing else: no address, no file name, no
/// folder, so a forged request cannot make it fetch or write anything but the one reviewed font.
/// </summary>
public static class FontHost
{
    public const string Command = "--terminal-font";
    public const string InstallOp = "install";
    public const string RemoveOp = "remove";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task<int> RunAsync(string[] args, FontInstaller? installer = null)
    {
        if (args.Length < 3 || args[0] != Command || args[2] is not (InstallOp or RemoveOp)) return 64;
        using var pipe = await HelperPipe.TryConnectAsync(args[1]).ConfigureAwait(false);
        if (pipe is null) return 65;
        var reader = new PipeLineReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        // Progress is reported from the download loop, so lines go out one at a time and in order.
        void Send(FontMessage message)
        {
            lock (writer)
            {
                try { writer.WriteLine(JsonSerializer.Serialize(message, Json)); }
                catch (IOException) { }
            }
        }

        Send(new("ready"));
        using var cancel = new CancellationTokenSource();
        // A line from the app, or a pipe that closes, means stop: nothing keeps installing with administrator rights once nobody is watching.
        _ = Task.Run(async () =>
        {
            try { await reader.ReadLineAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            try { cancel.Cancel(); } catch (ObjectDisposedException) { }
        });
        try
        {
            installer ??= new FontInstaller(DownloadAsync, new WindowsFontStore());
            var progress = new Relay(step => Send(new("progress", step.Step, Percent: step.Percent)));
            var result = args[2] == InstallOp ? await installer.InstallAsync(cancel.Token, progress).ConfigureAwait(false) : installer.Remove(progress);
            Send(new(result.Ok ? "done" : "failed", result.Message, result.Detail));
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Send(new("failed", "The font could not be installed.", ex.Message));
            return 1;
        }
    }

    /// <summary>Runs on the reporting thread, unlike <see cref="Progress{T}"/>, so the order of lines is the order of events.</summary>
    private sealed class Relay(Action<FontProgress> action) : IProgress<FontProgress> { public void Report(FontProgress value) => action(value); }

    private static async Task<byte[]> DownloadAsync(Action<long, long?> report, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TerminalKit.FontUrl);
        request.Headers.UserAgent.ParseAdd("TidyWindow");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total > TerminalKit.FontMaxBytes) throw new IOException("The download is larger than the font package should be.");
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > TerminalKit.FontMaxBytes) throw new IOException("The download is larger than the font package should be.");
            buffer.Write(chunk, 0, read);
            report(buffer.Length, total);
        }
        return buffer.ToArray();
    }
}

/// <summary>The app side: starts the elevated font helper through the Windows permission prompt and waits for its answer.</summary>
public sealed class FontSession(Func<string, string, Task> launch)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static FontSession Elevated(string helperPath) => new((pipe, op) => HelperPipe.LaunchElevated(helperPath, FontHost.Command, pipe, op));

    /// <param name="onAllowed">Called once the person has accepted the permission prompt and the helper is running.</param>
    /// <param name="progress">Told what the helper is doing as it goes. Called from a background thread unless it is a <see cref="Progress{T}"/> made on the interface thread.</param>
    public Task<TerminalResult> InstallAsync(Action? onAllowed = null, IProgress<FontProgress>? progress = null, CancellationToken cancellationToken = default) => RunAsync(FontHost.InstallOp, onAllowed, progress, cancellationToken);

    public Task<TerminalResult> RemoveAsync(Action? onAllowed = null, IProgress<FontProgress>? progress = null, CancellationToken cancellationToken = default) => RunAsync(FontHost.RemoveOp, onAllowed, progress, cancellationToken);

    private async Task<TerminalResult> RunAsync(string op, Action? onAllowed, IProgress<FontProgress>? progress, CancellationToken cancellationToken)
    {
        var name = HelperPipe.NewName("font");
        await using var server = HelperPipe.CreateServer(name);
        // Starting an elevated process waits for the permission prompt, so it never runs on the caller's thread and has no timer of its own.
        var launched = Task.Run(() => launch(name, op), CancellationToken.None);
        var connecting = server.WaitForConnectionAsync(cancellationToken);
        if (await Task.WhenAny(connecting, launched).ConfigureAwait(false) != connecting)
        {
            if (HelperPipe.WasDeclined(launched)) return new(false, "Administrator permission was declined, so the font was not changed.");
            if (launched.IsFaulted) return new(false, "The administrator helper could not be started.", launched.Exception!.GetBaseException().Message);
            return new(false, "The administrator helper stopped before it could report back.");
        }
        await connecting.ConfigureAwait(false);

        var reader = new PipeLineReader(server);
        var writer = new StreamWriter(server) { AutoFlush = true };
        using var stop = cancellationToken.Register(() => { try { writer.WriteLine("cancel"); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
        FontMessage? last = null;
        string? broken = null;
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                FontMessage? message;
                try { message = JsonSerializer.Deserialize<FontMessage>(line, Json); }
                catch (JsonException) { continue; }
                if (message is null) continue;
                if (message.Kind == "ready") { try { onAllowed?.Invoke(); } catch (Exception) { /* A display callback must not end the conversation. */ } }
                else if (message.Kind == "progress") { try { progress?.Report(new(message.Message ?? "", message.Percent)); } catch (Exception) { } }
                else last = message;
            }
        }
        catch (IOException ex) { broken = ex.Message; }
        try { await launched.ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { broken ??= ex.Message; }
        return last switch
        {
            { Kind: "done" } => new(true, last.Message ?? "Done.", last.Detail ?? ""),
            { Kind: "failed" } => new(false, last.Message ?? "The font could not be changed.", last.Detail ?? ""),
            _ => new(false, "The administrator helper stopped before it finished.", broken ?? "")
        };
    }
}
