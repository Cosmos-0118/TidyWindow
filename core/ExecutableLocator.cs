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
        directories.Add(manager switch
        {
            ManagerKind.Winget => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
            ManagerKind.Chocolatey => Path.Combine(Environment.GetEnvironmentVariable("ChocolateyInstall") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey"), "bin"),
            _ => Path.Combine(Environment.GetEnvironmentVariable("SCOOP") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop"), "shims")
        });
        return directories.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.Combine(Environment.ExpandEnvironmentVariables(x!.Trim().Trim('"')), name))
            .FirstOrDefault(File.Exists);
    }
}
