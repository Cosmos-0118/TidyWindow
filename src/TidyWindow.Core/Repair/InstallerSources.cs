using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace TidyWindow.Next.Core;

/// <summary>What the person allowed for one repair: fetching the original installer from WinGet, and folders to look in for it.</summary>
public sealed record RepairOptions(bool Download = false, IReadOnlyList<string>? Folders = null);

/// <summary>A package WinGet itself matched to a program that is installed, at the version that is installed.</summary>
public sealed record WingetMatch(string Id, string Name, string Version);

/// <summary>Gets the original installer of a program whose own copy is gone. Every file it returns is checked against the program before it is used.</summary>
public interface IInstallerFetcher
{
    /// <summary>The WinGet package for an installed program, only when WinGet lists that exact name at that exact version.</summary>
    WingetMatch? Match(string name, string version);
    /// <summary>Downloads that exact version into <paramref name="folder"/> and returns the files. WinGet checks them against the hash in its manifest.</summary>
    IReadOnlyList<string> Download(WingetMatch match, string folder, string? architecture, CancellationToken cancellationToken);
    /// <summary>Runs that version's own installer over the installed one, which gives programs such as Inno Setup and NSIS ones their uninstaller back.</summary>
    RunResult Reinstall(WingetMatch match, CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
public sealed partial class WingetFetcher : IInstallerFetcher
{
    public WingetMatch? Match(string name, string version)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) return null;
        var result = Run(TimeSpan.FromSeconds(60), default, "list", "--name", name, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity");
        // WinGet fills the Id column only for a program it has matched to a package, and its Version column is the version it worked out
        // for the installed program (Python's entry says 3.13.16150.0, WinGet says 3.13.16), which is the one its package list uses.
        // It exits non-zero when it has no such installed program, which is when the search below is the only way to a package.
        foreach (var row in result.ExitCode == 0 ? PackageOutputParser.Parse(ManagerKind.Winget, result.Output, 20) : [])
            if (PackageOutputParser.IsSafeId(row.Id) && PackageOutputParser.IsSafeId(row.Version) && string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))
                return new(row.Id, row.Name, row.Version);
        return Search(name, version);
    }

    /// <summary>
    /// WinGet does not always tie an installed program to its package (a bundle run from python.org's setup is listed as "Python 3.12.8 (64-bit)", the package is "Python 3.12").
    /// Looks for a package whose exact name is the program's name with its architecture, patch or version dropped, and takes it only if that package has the installed version.
    /// </summary>
    private static WingetMatch? Search(string name, string version)
    {
        var wanted = VersionToken().Match(name) is { Success: true } token ? token.Value : version;
        if (!PackageOutputParser.IsSafeId(wanted)) return null;
        foreach (var query in Queries(name))
        {
            var found = Run(TimeSpan.FromSeconds(60), default, "search", "--name", query, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity");
            if (found.ExitCode != 0) continue;
            foreach (var row in PackageOutputParser.Parse(ManagerKind.Winget, found.Output, 20))
            {
                if (!PackageOutputParser.IsSafeId(row.Id) || !string.Equals(row.Name, query, StringComparison.OrdinalIgnoreCase)) continue;
                var versions = Run(TimeSpan.FromSeconds(60), default, "show", "--id", row.Id, "--exact", "--source", "winget", "--versions", "--accept-source-agreements", "--disable-interactivity");
                if (versions.ExitCode == 0 && versions.Output.Split('\n').Any(x => string.Equals(x.Trim(), wanted, StringComparison.OrdinalIgnoreCase)))
                    return new(row.Id, row.Name, wanted);
            }
        }
        return null;
    }

    /// <summary>The name without its "(64-bit)", then with the last version part dropped one at a time, and then the last word.</summary>
    internal static IEnumerable<string> Queries(string name)
    {
        var words = ParenthesisPart().Replace(name, "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0)
        {
            yield return string.Join(' ', words);
            var last = words[^1];
            if (VersionToken().IsMatch(last) && last.LastIndexOf('.') is > 0 and var dot) words[^1] = last[..dot];
            else words.RemoveAt(words.Count - 1);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial System.Text.RegularExpressions.Regex VersionToken();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex ParenthesisPart();

    public IReadOnlyList<string> Download(WingetMatch match, string folder, string? architecture, CancellationToken cancellationToken)
    {
        if (!PackageOutputParser.IsSafeId(match.Id) || !PackageOutputParser.IsSafeId(match.Version)) return [];
        Directory.CreateDirectory(folder);
        var arguments = new List<string> { "download", "--id", match.Id, "--version", match.Version, "--exact", "--source", "winget", "--download-directory", folder, "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" };
        if (architecture is "x64" or "x86") arguments.AddRange(["--architecture", architecture]);
        var result = Run(TimeSpan.FromMinutes(15), cancellationToken, [.. arguments]);
        if (result.ExitCode != 0) return [];
        return Directory.EnumerateFiles(folder).Where(x => Path.GetExtension(x).ToLowerInvariant() is ".msi" or ".exe" or ".msix" or ".zip").ToList();
    }

    public RunResult Reinstall(WingetMatch match, CancellationToken cancellationToken)
    {
        if (!PackageOutputParser.IsSafeId(match.Id) || !PackageOutputParser.IsSafeId(match.Version)) return new(87, "That package id is not safe to pass on.");
        return Run(TimeSpan.FromMinutes(20), cancellationToken, "install", "--id", match.Id, "--version", match.Version, "--exact", "--source", "winget", "--force", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity");
    }

    private static RunResult Run(TimeSpan timeout, CancellationToken cancellationToken, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("winget.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return new(1, "WinGet could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            using var stop = cancellationToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            if (!process.WaitForExit(timeout)) { try { process.Kill(true); } catch (InvalidOperationException) { } return new(1460, "WinGet did not finish in time."); }
            return new(process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return new(1, "WinGet is not available: " + ex.Message); }
    }
}
