using System.Text.Json;

namespace TidyWindow.Next.Core;

public sealed record ManagerLocation(string Root, string Configuration);

/// <param name="variables">Reads an environment variable from a scope, or from this process when the scope is null. Replaceable in tests.</param>
public sealed class ManagerLocations(Func<string, EnvironmentVariableTarget?, string?>? variables = null)
{
    private readonly Func<string, EnvironmentVariableTarget?, string?> variables = variables ?? ((name, scope) => scope is { } target ? Environment.GetEnvironmentVariable(name, target) : Environment.GetEnvironmentVariable(name));

    public ManagerLocation Get(ManagerKind manager)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Chocolatey runs with administrator rights, so only the PC-wide setting (which only administrators can change) says where it is. A setting
        // in this account's own scope is not consulted: it is what any program of this account can write, and it would point an elevated run anywhere.
        if (manager == ManagerKind.Chocolatey) return new(ChocolateyRoot(), "");
        if (manager == ManagerKind.Winget)
            return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"), "");
        var configuration = Path.Combine(variables("XDG_CONFIG_HOME", null) ?? Path.Combine(profile, ".config"), "scoop", "config.json");
        string? configuredRoot = null;
        try
        {
            if (File.Exists(configuration))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configuration));
                if (document.RootElement.TryGetProperty("root_path", out var root)) configuredRoot = root.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(variables("SCOOP", EnvironmentVariableTarget.User) ?? variables("SCOOP", EnvironmentVariableTarget.Machine)
            ?? variables("SCOOP", null) ?? configuredRoot ?? Path.Combine(profile, "scoop"), configuration);
    }

    private string ChocolateyRoot() => variables("ChocolateyInstall", EnvironmentVariableTarget.Machine) is { Length: > 0 } root ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");

    /// <summary>
    /// Why Chocolatey must not be used from this account, or null. A ChocolateyInstall setting in the account's own scope (or in this process) that
    /// names another folder would send choco.exe, started with administrator rights, to a folder the account controls for its helpers and package scripts.
    /// </summary>
    public string? ChocolateyAccountProblem()
    {
        var root = ChocolateyRoot();
        foreach (var scope in new EnvironmentVariableTarget?[] { EnvironmentVariableTarget.User, null })
            if (variables("ChocolateyInstall", scope) is { Length: > 0 } other && !SamePath(other, root))
                return $"A ChocolateyInstall setting for this account points Chocolatey at {other}, not at the PC-wide installation at {root}. Chocolatey runs with administrator rights, so TidyWindow does not use it until that setting is removed.";
        return null;
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return false; }
    }

    public static void ValidateRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("The package manager installation path must be absolute.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var protectedPaths = new[] { Path.GetPathRoot(full), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) };
        if (protectedPaths.Any(x => string.Equals(full, Path.TrimEndingDirectorySeparator(x ?? ""), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The configured installation path is too broad to repair automatically.");
    }
}
