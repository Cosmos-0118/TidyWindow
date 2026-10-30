namespace TidyWindow.Next.Core;

public sealed class ExecutableLocator : IExecutableLocator
{
    public string PowerShell => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => Path.Combine(Environment.ExpandEnvironmentVariables(x.Trim().Trim('"')), "pwsh.exe"))
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"))
        .FirstOrDefault(File.Exists) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public string? Find(ManagerKind manager)
    {
        var name = manager switch { ManagerKind.Winget => "winget.exe", ManagerKind.Chocolatey => "choco.exe", _ => "scoop.ps1" };
        var directories = new List<string?>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            directories.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(Path.PathSeparator));
        var location = new ManagerLocations().Get(manager);
        directories.Add(manager switch
        {
            ManagerKind.Winget => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
            ManagerKind.Chocolatey => Path.Combine(location.Root, "bin"),
            _ => Path.Combine(location.Root, "shims")
        });
        return directories.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.Combine(Environment.ExpandEnvironmentVariables(x!.Trim().Trim('"')), name))
            .Append(Path.Combine(location.Root, manager == ManagerKind.Scoop ? "apps/scoop/current/bin/scoop.ps1" : name))
            .FirstOrDefault(File.Exists);
    }
}
