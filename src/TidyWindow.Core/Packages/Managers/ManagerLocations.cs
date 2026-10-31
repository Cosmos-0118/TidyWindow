using System.Text.Json;

namespace TidyWindow.Next.Core;

public sealed record ManagerLocation(string Root, string Configuration);

public sealed class ManagerLocations
{
    public ManagerLocation Get(ManagerKind manager)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (manager == ManagerKind.Chocolatey)
            return new(Environment.GetEnvironmentVariable("ChocolateyInstall", EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable("ChocolateyInstall", EnvironmentVariableTarget.Machine)
                ?? Environment.GetEnvironmentVariable("ChocolateyInstall")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey"), "");
        if (manager == ManagerKind.Winget)
            return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"), "");
        var configuration = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(profile, ".config"), "scoop", "config.json");
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
        return new(Environment.GetEnvironmentVariable("SCOOP", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("SCOOP", EnvironmentVariableTarget.Machine)
            ?? Environment.GetEnvironmentVariable("SCOOP") ?? configuredRoot ?? Path.Combine(profile, "scoop"), configuration);
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
