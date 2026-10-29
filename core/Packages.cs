namespace TidyWindow.Next.Core;

public enum ManagerKind { Winget, Chocolatey, Scoop }
public enum ManagerHealth { Ready, Missing, Unavailable, ConnectionIssue, Restricted }
public enum ManagerProblem { None, NotInstalled, Executable, Configuration, Source, Network, Policy }

public sealed record ManagerDefinition(ManagerKind Kind, string Name, string Description, string SetupUrl)
{
    public static IReadOnlyList<ManagerDefinition> All { get; } =
    [
        new(ManagerKind.Winget, "WinGet", "The Windows package manager", "https://learn.microsoft.com/windows/package-manager/winget/"),
        new(ManagerKind.Chocolatey, "Chocolatey", "Community-powered Windows software", "https://chocolatey.org/install"),
        new(ManagerKind.Scoop, "Scoop", "Command-line tools, installed for you", "https://scoop.sh/")
    ];
}

public sealed record ManagerStatus(ManagerDefinition Definition, ManagerHealth Health, string Version, string? Executable, string Detail, ManagerProblem Problem = ManagerProblem.None);
public sealed record Package(ManagerKind Manager, string Id, string Name, string Version, string Source)
{
    public string Key => $"{Manager}:{Id}";
    public string ManagerName => ManagerDefinition.All.Single(x => x.Kind == Manager).Name;
}
public sealed record SearchResult(ManagerKind Manager, IReadOnlyList<Package> Packages, string? Error = null);
public sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, bool Elevate = false);
public sealed record CommandResult(int ExitCode, string Output, string Error);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default);
}

public interface IExecutableLocator
{
    string? Find(ManagerKind manager);
    string PowerShell { get; }
}
