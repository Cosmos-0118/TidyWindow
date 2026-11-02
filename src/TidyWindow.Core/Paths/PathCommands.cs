using System.Buffers.Binary;
using System.Diagnostics;

namespace TidyWindow.Next.Core;

/// <summary>One program file a command name could mean, in the order Windows would find it.</summary>
public sealed record CommandCandidate(string File, PathScope Scope, int Index, bool Alias);

public sealed record CommandLookup(string Command, IReadOnlyList<CommandCandidate> Candidates)
{
    public CommandCandidate? Winner => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>
/// Answers "which program runs when I type this in a terminal?" the way a terminal decides it: folders in PATH order, then each extension in
/// PATHEXT order. The current folder, which cmd tries first, and the App Paths registry key, which the Run box and Explorer also look at,
/// are not part of the answer.
/// </summary>
public sealed class CommandResolver(Func<string, bool> fileExists, Func<string, bool> isAlias, IReadOnlyList<string> extensions)
{
    public static readonly string[] Common = ["python", "python3", "pip", "node", "npm", "java", "javac", "dotnet", "git", "pwsh", "winget", "go", "rustc", "cargo"];

    public static IReadOnlyList<string> DefaultExtensions() =>
        (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public CommandLookup Find(string command, PathSnapshot snapshot)
    {
        command = command.Trim();
        string[] names = command.Length == 0 ? Array.Empty<string>() : Path.HasExtension(command) && extensions.Contains(Path.GetExtension(command), StringComparer.OrdinalIgnoreCase)
            ? [command]
            : extensions.Select(extension => command + extension.ToLowerInvariant()).ToArray();
        var found = new List<CommandCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Search)
        {
            if (!entry.Exists) continue;
            foreach (var name in names)
            {
                var file = Path.Combine(entry.Folder, name);
                if (!seen.Add(file) || !fileExists(file)) continue;
                found.Add(new CommandCandidate(file, entry.Scope, entry.Index, isAlias(file)));
            }
        }
        return new(command, found);
    }
}

/// <param name="Skipped">The program was not asked, because it would have opened a window instead of answering.</param>
public sealed record ProbeResult(string Version, bool StoreStub, bool Skipped = false);

/// <summary>What a program file is, read from its header without running it.</summary>
public enum ProgramKind { Unknown, Console, Windowed, Script }

public static class ProgramFile
{
    /// <summary>
    /// Reads a program's kind from its file header: a console program, a windowed one, a script, or something that is none of these. The
    /// header, not the name, decides: whether a program takes a terminal or opens a window is written there by whoever built it.
    /// </summary>
    public static ProgramKind Kind(string file)
    {
        var extension = Path.GetExtension(file);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)) return ProgramKind.Script;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            Span<byte> dos = stackalloc byte[64];
            if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != (byte)'M' || dos[1] != (byte)'Z') return ProgramKind.Unknown;
            var offset = BinaryPrimitives.ReadInt32LittleEndian(dos[60..]);
            // The PE signature, the 20-byte file header, then the optional header, whose subsystem field sits at offset 68 in both its 32-bit and 64-bit forms.
            Span<byte> pe = stackalloc byte[94];
            if (offset < dos.Length || offset > stream.Length - pe.Length) return ProgramKind.Unknown;
            stream.Position = offset;
            if (stream.ReadAtLeast(pe, pe.Length, throwOnEndOfStream: false) < pe.Length || pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0) return ProgramKind.Unknown;
            if (BinaryPrimitives.ReadUInt16LittleEndian(pe[24..]) is not (0x10B or 0x20B)) return ProgramKind.Unknown;
            return BinaryPrimitives.ReadUInt16LittleEndian(pe[92..]) switch { 3 => ProgramKind.Console, 2 => ProgramKind.Windowed, _ => ProgramKind.Unknown };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return ProgramKind.Unknown; }
    }
}

/// <summary>
/// Asks a program for its version. Only ever runs <c>--version</c> (or <c>-version</c> for Java), with a short limit and no window of its own.
/// Running a program at all is only safe for ones built to answer in a terminal, so a windowed program, or a file whose header cannot be
/// read, is reported as not asked rather than started and killed four seconds later with whatever it opened.
/// </summary>
public static class CommandProbe
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);

    /// <param name="alias">The file is a Store app alias. Its header cannot be read, and running it is the only way to tell a placeholder that opens the Store from the real program.</param>
    public static async Task<ProbeResult> RunAsync(string command, string file, CancellationToken cancellationToken = default, bool alias = false)
    {
        if (!alias && ProgramFile.Kind(file) is not (ProgramKind.Console or ProgramKind.Script)) return new("", false, Skipped: true);
        var start = new ProcessStartInfo(file)

        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.ArgumentList.Add(command is "java" or "javac" ? "-version" : "--version");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The program could not be started.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Limit);
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return new("", false);
            }
            var text = await output.ConfigureAwait(false) + "\n" + await errors.ConfigureAwait(false);
            return Parse(text, process.ExitCode);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return new("", false); }
    }

    /// <summary>The Microsoft Store placeholder for Python answers with a "not found" message instead of a version.</summary>
    public static ProbeResult Parse(string text, int exitCode)
    {
        if (exitCode == 9009 || text.Contains("Microsoft Store", StringComparison.OrdinalIgnoreCase)) return new("", true);
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var from = line.IndexOf(" from ", StringComparison.Ordinal);
        if (from > 0) line = line[..from];
        return new(line.Length > 70 ? line[..70] : line, false);
    }
}

/// <summary>Plain-language warnings that need more than the PATH list itself.</summary>
public static class PathAdvice
{
    public static IReadOnlyList<string> JavaHome(string? javaHome, CommandLookup java, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(javaHome)) return [];
        var notes = new List<string>();
        var home = javaHome.Trim().Trim('"').TrimEnd('\\');
        if (!directoryExists(home))
        {
            notes.Add($"JAVA_HOME points to a folder that is gone ({home}). Build tools such as Maven and Gradle will fail until it is changed.");
            return notes;
        }
        if (java.Winner is { } winner && !winner.File.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase))
            notes.Add($"JAVA_HOME is {home}, but typing java in a terminal runs {winner.File}. Build tools and terminals can end up using different versions of Java.");
        return notes;
    }
}
