using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>What may run with administrator rights, and where each manager is looked for.</summary>
public sealed class ElevatedProgramLocatorTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "tidywindow-elevated-" + Guid.NewGuid().ToString("N"));

    public ElevatedProgramLocatorTests() => Directory.CreateDirectory(temp);
    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    [Fact]
    public void AProgramInAProfileOrTemporaryFolderMustNotRunWithAdministratorRights()
    {
        if (!OperatingSystem.IsWindows()) return;
        var planted = Path.Combine(temp, "choco.exe");
        File.WriteAllText(planted, "not really");
        var problem = ElevatedProgram.ProtectedPathProblem(planted);
        Assert.NotNull(problem);
        Assert.Contains("administrator rights", problem);
        Assert.False(ElevatedProgram.IsProtectedLocation(planted));
        Assert.NotNull(ElevatedProgram.ProtectedPathProblem(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe")));
    }

    [Fact]
    public void TheTwoWaysOfAskingGiveTheSameAnswerForTheSameFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var planted = Path.Combine(temp, "tool.exe");
        File.WriteAllText(planted, "not really");
        var link = Path.Combine(temp, "link.exe");
        // A symbolic link needs a privilege an ordinary account may not have; the file itself is what matters when it cannot be made.
        var linked = false;
        try { File.CreateSymbolicLink(link, Path.Combine(Environment.SystemDirectory, "cmd.exe")); linked = true; } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        foreach (var file in new[] { planted, Path.Combine(Environment.SystemDirectory, "cmd.exe"), ElevatedProgram.WindowsPowerShell, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "..", "Windows", "System32", "cmd.exe") })
            Assert.Equal(ElevatedProgram.ProtectedPathProblem(file) is null, ElevatedProgram.Resolve(Path.GetFullPath(file)).Problem is null);
        if (linked)
        {
            Assert.Contains("is a link", ElevatedProgram.ProtectedPathProblem(link));
            Assert.Contains("is a link", ElevatedProgram.Resolve(link).Problem);
        }
    }

    [Fact]
    public void OnlyAFullPathToAnExistingFileOnThisPcIsConsidered()
    {
        Assert.Contains("not a full path", ElevatedProgram.ProtectedPathProblem("choco.exe"));
        Assert.Contains("not a full path", ElevatedProgram.ProtectedPathProblem(@"\\server\share\choco.exe"));
        Assert.Contains("not a full path", ElevatedProgram.ProtectedPathProblem(""));
        if (OperatingSystem.IsWindows()) Assert.Contains("does not exist", ElevatedProgram.ProtectedPathProblem(@"C:\Windows\System32\tidywindow-nope.exe"));
    }

    [Fact]
    public void WindowsOwnProgramsMayRunWithAdministratorRights()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(ElevatedProgram.ProtectedPathProblem(ElevatedProgram.WindowsPowerShell));
        Assert.True(ElevatedProgram.IsProtectedLocation(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
    }

    [Fact]
    public void TheElevatedPowerShellHostIsOneWindowsOrItsInstallerPutInPlace()
    {
        var host = ElevatedProgram.PowerShell;
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        Assert.True(host.Equals(programFiles, StringComparison.OrdinalIgnoreCase) || host.Equals(ElevatedProgram.WindowsPowerShell, StringComparison.OrdinalIgnoreCase), host);
        Assert.Equal(host, new ExecutableLocator().PowerShell);
        if (OperatingSystem.IsWindows()) Assert.True(File.Exists(host));
    }

    [Fact]
    public void WingetForElevatedRunsComesFromTheAppInstallerPackageOrNotAtAll()
    {
        var winget = ElevatedProgram.Winget();
        if (winget is null) return;
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + Path.DirectorySeparatorChar;
        Assert.StartsWith(apps, winget, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft.DesktopAppInstaller_", winget);
        Assert.EndsWith("8wekyb3d8bbwe\\winget.exe", winget, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ElevatedProgram.ProtectedPathProblem(winget));
    }

    private static Func<string, EnvironmentVariableTarget?, string?> Variables(Dictionary<(string, EnvironmentVariableTarget?), string> values) =>
        (name, scope) => values.TryGetValue((name, scope), out var value) ? value : null;

    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    [Fact]
    public void ChocolateyIsTakenFromThePcWideInstallationBeforeAnythingOnAnAccountsPath()
    {
        var profileChoco = @"C:\Users\me\choco\bin\choco.exe";
        var pcWide = Path.Combine(ProgramData, "chocolatey", "bin", "choco.exe");
        var variables = Variables(new()
        {
            [("ChocolateyInstall", EnvironmentVariableTarget.User)] = @"C:\Users\me\choco",
            [("PATH", null)] = @"C:\Users\me\choco\bin;C:\Windows\System32",
            [("PATH", EnvironmentVariableTarget.User)] = @"C:\Users\me\choco\bin"
        });
        var both = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { profileChoco, pcWide };
        Assert.Equal(pcWide, new ExecutableLocator(variables, both.Contains).Find(ManagerKind.Chocolatey));
        // Only the account's copy exists: it is still found (so the health check can say why it is refused) but never ahead of a PC-wide one.
        Assert.Equal(profileChoco, new ExecutableLocator(variables, x => x.Equals(profileChoco, StringComparison.OrdinalIgnoreCase)).Find(ManagerKind.Chocolatey));
        // A PC-wide setting names the installation; the account's own setting is never consulted for it.
        var custom = Variables(new() { [("ChocolateyInstall", EnvironmentVariableTarget.Machine)] = @"D:\choco", [("ChocolateyInstall", EnvironmentVariableTarget.User)] = @"C:\Users\me\choco" });
        Assert.Equal(@"D:\choco\bin\choco.exe", new ExecutableLocator(custom, x => x.StartsWith(@"D:\choco", StringComparison.OrdinalIgnoreCase) || both.Contains(x)).Find(ManagerKind.Chocolatey));
        Assert.Equal(@"D:\choco", new ManagerLocations(custom).Get(ManagerKind.Chocolatey).Root);
        Assert.Equal(Path.Combine(ProgramData, "chocolatey"), new ManagerLocations(variables).Get(ManagerKind.Chocolatey).Root);
    }

    [Fact]
    public void WingetsOwnAliasComesBeforeAnythingOnThePath()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        var shim = @"C:\Users\me\scoop\shims\winget.exe";
        var variables = Variables(new() { [("PATH", null)] = @"C:\Users\me\scoop\shims;C:\Windows\System32" });
        var both = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { alias, shim };
        Assert.Equal(alias, new ExecutableLocator(variables, both.Contains).Find(ManagerKind.Winget));
        Assert.Equal(shim, new ExecutableLocator(variables, x => x.Equals(shim, StringComparison.OrdinalIgnoreCase)).Find(ManagerKind.Winget));
        Assert.Null(new ExecutableLocator(variables, _ => false).Find(ManagerKind.Winget));
    }

    [Fact]
    public void AnAccountSettingThatSendsChocolateyElsewhereIsRefused()
    {
        var pcWide = Path.Combine(ProgramData, "chocolatey");
        Assert.Null(new ManagerLocations(Variables(new())).ChocolateyAccountProblem());
        Assert.Null(new ManagerLocations(Variables(new() { [("ChocolateyInstall", EnvironmentVariableTarget.User)] = pcWide + @"\" })).ChocolateyAccountProblem());
        Assert.Null(new ManagerLocations(Variables(new() { [("ChocolateyInstall", EnvironmentVariableTarget.Machine)] = @"D:\choco", [("ChocolateyInstall", null)] = @"d:\CHOCO\" })).ChocolateyAccountProblem());
        var refused = new ManagerLocations(Variables(new() { [("ChocolateyInstall", EnvironmentVariableTarget.User)] = @"C:\Users\me\choco" })).ChocolateyAccountProblem();
        Assert.NotNull(refused);
        Assert.Contains(@"C:\Users\me\choco", refused);
        Assert.Contains("administrator rights", refused);
        Assert.NotNull(new ManagerLocations(Variables(new() { [("ChocolateyInstall", null)] = @"C:\Users\me\choco" })).ChocolateyAccountProblem());
    }
}
