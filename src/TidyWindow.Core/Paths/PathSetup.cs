using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum ToolState { NotInPath, Working, NotInstalled }

/// <summary>
/// A tool that installs into a well-known folder. <paramref name="Folders"/> may use %VARIABLES% and a * in a folder name
/// (jdk*), <paramref name="FolderOnly"/> tools have no single program to look for, so the folder alone counts.
/// </summary>
public sealed record ToolSpec(string Name, string Command, string File, string[] Folders, bool FolderOnly = false);

public sealed record ToolSetup(string Name, string Command, ToolState State, string Folder, string Detail);

/// <summary>Finds installed developer tools whose folder is missing from PATH, which is why typing their command says "not recognized".</summary>
public sealed class PathSetup(Func<string, string> expand, Func<string, bool> directoryExists, Func<string, bool> fileExists, Func<string, IEnumerable<string>> subdirectories)
{
    public static readonly IReadOnlyList<ToolSpec> Known =
    [
        new("Go", "go", "go.exe", [@"%ProgramFiles%\Go\bin", @"%SystemDrive%\Go\bin"]),
        new("Go tools", "go install", "", [@"%USERPROFILE%\go\bin"], FolderOnly: true),
        new("Rust (Cargo)", "cargo", "cargo.exe", [@"%USERPROFILE%\.cargo\bin"]),
        new("Node.js", "node", "node.exe", [@"%ProgramFiles%\nodejs"]),
        new("npm global tools", "npm install -g", "", [@"%APPDATA%\npm"], FolderOnly: true),
        new("Git", "git", "git.exe", [@"%ProgramFiles%\Git\cmd", @"%ProgramFiles(x86)%\Git\cmd", @"%LOCALAPPDATA%\Programs\Git\cmd"]),
        new("Python", "python", "python.exe", [@"%LOCALAPPDATA%\Programs\Python\Python3*", @"%ProgramFiles%\Python3*"]),
        new("Python scripts", "pip", "pip.exe", [@"%LOCALAPPDATA%\Programs\Python\Python3*\Scripts", @"%ProgramFiles%\Python3*\Scripts"]),
        new("Java (JDK)", "java", "java.exe", [@"%ProgramFiles%\Java\jdk*\bin", @"%ProgramFiles%\Eclipse Adoptium\jdk*\bin", @"%ProgramFiles%\Microsoft\jdk*\bin", @"%ProgramFiles%\Zulu\zulu*\bin"]),
        new(".NET", "dotnet", "dotnet.exe", [@"%ProgramFiles%\dotnet"]),
        new(".NET tools", "dotnet tool", "", [@"%USERPROFILE%\.dotnet\tools"], FolderOnly: true),
        new("PowerShell 7", "pwsh", "pwsh.exe", [@"%ProgramFiles%\PowerShell\7"]),
        new("GitHub CLI", "gh", "gh.exe", [@"%ProgramFiles%\GitHub CLI"]),
        new("CMake", "cmake", "cmake.exe", [@"%ProgramFiles%\CMake\bin"]),
        new("Docker", "docker", "docker.exe", [@"%ProgramFiles%\Docker\Docker\resources\bin"]),
        new("Deno", "deno", "deno.exe", [@"%USERPROFILE%\.deno\bin"]),
        new("Bun", "bun", "bun.exe", [@"%USERPROFILE%\.bun\bin"]),
        new("VS Code", "code", "code.cmd", [@"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin", @"%ProgramFiles%\Microsoft VS Code\bin"]),
        new("Ruby", "ruby", "ruby.exe", [@"%SystemDrive%\Ruby*\bin"]),
    ];

    /// <param name="resolve">Looks a command up on the PATH Windows would use right now.</param>
    public IReadOnlyList<ToolSetup> Find(PathSnapshot snapshot, Func<string, CommandLookup> resolve)
    {
        var inPath = snapshot.Search.Select(x => PathAnalyzer.Key(x.Folder)).ToHashSet();
        var found = new List<ToolSetup>();
        foreach (var spec in Known)
        {
            var folder = Locate(spec);
            if (folder is not null && inPath.Contains(PathAnalyzer.Key(folder)))
            {
                found.Add(new(spec.Name, spec.Command, ToolState.Working, folder, "Already in PATH."));
                continue;
            }
            if (!spec.FolderOnly && resolve(spec.Command).Winner is { } winner)
            {
                found.Add(new(spec.Name, spec.Command, ToolState.Working, Path.GetDirectoryName(winner.File) ?? "", "Already works."));
                continue;
            }
            found.Add(folder is null
                ? new(spec.Name, spec.Command, ToolState.NotInstalled, "", "Not found on this PC.")
                : new(spec.Name, spec.Command, ToolState.NotInPath, folder, spec.FolderOnly ? "Programs installed here cannot be run by name yet." : $"Installed, but typing {spec.Command} says it is not recognized."));
        }
        return found.OrderBy(x => x.State).ToList();
    }

    private string? Locate(ToolSpec spec)
    {
        foreach (var template in spec.Folders)
            foreach (var folder in Candidates(template))
                if (directoryExists(folder) && (spec.FolderOnly || fileExists(Path.Combine(folder, spec.File)))) return folder;
        return null;
    }

    /// <summary>Expands the variables, then each * folder into the real ones, newest first.</summary>
    private IEnumerable<string> Candidates(string template)
    {
        var parts = expand(template).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) yield break;
        IEnumerable<string> current = [parts[0].EndsWith(':') ? parts[0] + "\\" : parts[0]];
        foreach (var part in parts.Skip(1))
        {
            var segment = part;
            current = current.SelectMany(parent => Step(parent, segment)).ToList();
        }
        foreach (var folder in current) yield return folder;
    }

    private IEnumerable<string> Step(string parent, string segment)
    {
        if (!segment.Contains('*')) return [Path.Combine(parent, segment)];
        return subdirectories(parent).Where(d => Matches(Path.GetFileName(d), segment)).OrderByDescending(d => Natural(Path.GetFileName(d)), StringComparer.OrdinalIgnoreCase);
    }

    private static bool Matches(string name, string pattern) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    // jdk-25 sorts above jdk-8.
    private static string Natural(string name) => Regex.Replace(name, @"\d+", m => m.Value.PadLeft(10, '0'));
}
