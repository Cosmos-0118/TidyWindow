using System.Diagnostics;

namespace TidyWindow.Next.Core;

/// <summary>One program file a command name could mean, in the order Windows would find it.</summary>
public sealed record CommandCandidate(string File, PathScope Scope, int Index, bool Alias);

public sealed record CommandLookup(string Command, IReadOnlyList<CommandCandidate> Candidates)
{
    public CommandCandidate? Winner => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>Answers "which program runs when I type this?" the way Windows decides it: folders in PATH order, then each extension in PATHEXT order.</summary>
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

public sealed record ProbeResult(string Version, bool StoreStub);

/// <summary>Asks a program for its version. Only ever runs <c>--version</c> (or <c>-version</c> for Java), with a short limit, never a window.</summary>
public static class CommandProbe
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);

    public static async Task<ProbeResult> RunAsync(string command, string file, CancellationToken cancellationToken = default)
    {
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
