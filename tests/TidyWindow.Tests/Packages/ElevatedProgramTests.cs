using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>A program runs with administrator rights only from a folder that accounts without them cannot change: nothing planted by a program running as the person may be approved into an administrator's hands.</summary>
public sealed class ElevatedProgramTests
{
    private static (string FileName, string? Problem) Resolve(string fileName, Func<string, bool>? exists = null, Func<string, string?>? alias = null, Func<string, ElevatedProgram.FolderAccess>? access = null) =>
        ElevatedProgram.Resolve(fileName, exists ?? (_ => true), alias ?? (_ => null), access ?? (_ => ElevatedProgram.FolderAccess.Protected));

    [Fact]
    public void AProgramInAProtectedFolderMayRun()
    {
        var (file, problem) = Resolve(@"C:\Program Files\Vendor\tool.exe");
        Assert.Equal(@"C:\Program Files\Vendor\tool.exe", file);
        Assert.Null(problem);
    }

    [Fact]
    public void AProgramThatIsNotThereMayNotRun() => Assert.Contains("tool.exe could not be found", Resolve(@"C:\Program Files\Vendor\tool.exe", exists: _ => false).Problem);

    [Fact]
    public void AFolderOrdinaryAccountsCanChangeIsRefusedAndNamed()
    {
        var (_, problem) = Resolve(@"D:\Tools\tool.exe", access: _ => ElevatedProgram.FolderAccess.Open);
        Assert.Contains("tool.exe is installed in a folder that any program on this PC could change (D:\\Tools), so TidyWindow will not run it with administrator rights.", problem);
    }

    [Fact]
    public void AFolderWhosePermissionsCannotBeReadIsRefusedToo() =>
        Assert.Contains("could not check who can change the folder tool.exe is in (D:\\Tools)", Resolve(@"D:\Tools\tool.exe", access: _ => ElevatedProgram.FolderAccess.Unknown).Problem);

    [Fact]
    public void AnythingUnderTheProfileOrTheTemporaryFolderIsRefusedWhateverItsPermissionsSay()
    {
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
            Assert.Contains("any program on this PC could change", Resolve(Path.Combine(folder, "tool.exe")).Problem);
    }

    [Fact]
    public void AnAliasIsReplacedByThePackagedProgramItStandsForAndThatProgramIsJudged()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        const string program = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0_x64__8wekyb3d8bbwe\winget.exe";
        var judged = new List<string>();
        var (file, problem) = Resolve(alias, alias: x => x == alias ? program : null, access: folder => { judged.Add(folder); return ElevatedProgram.FolderAccess.Protected; });
        Assert.Equal(program, file);
        Assert.Null(problem);
        Assert.Equal([Path.GetDirectoryName(program)!], judged);
        // The alias itself sits in the profile; without the program it stands for it would be refused like anything else there.
        Assert.Contains("any program on this PC could change", Resolve(alias).Problem);
        Assert.Contains("not there", Resolve(alias, exists: x => x == alias, alias: _ => program).Problem);
    }

    [Fact]
    public void ABareNameIsLookedUpWhereWindowsWouldStartItFrom()
    {
        var system = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var (file, problem) = Resolve("cmd.exe", exists: x => x.Equals(system, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(system, file);
        Assert.Null(problem);
        Assert.Contains("could not be found", Resolve("nothing-here.exe", exists: _ => false).Problem);
    }

    [Fact]
    public void ACheckedCommandCarriesTheResolvedProgramOrSaysWhyItMustNotRun()
    {
        var plain = new CommandSpec("x.exe", [], TimeSpan.FromMinutes(1));
        Assert.Same(plain, ElevatedProgram.Checked(plain, new Locator { Problem = "no" }));
        var elevated = plain with { Elevate = true };
        var checkedCommand = ElevatedProgram.Checked(elevated, new Locator { Resolved = @"C:\real\x.exe" });
        Assert.Equal(@"C:\real\x.exe", checkedCommand.FileName);
        Assert.True(checkedCommand.Elevate);
        const string reason = @"x.exe is installed in a folder that any program on this PC could change (C:\Users\me), so TidyWindow will not run it with administrator rights.";
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => ElevatedProgram.Checked(elevated, new Locator { Problem = reason })).Message);
        // A script the program would run is judged the same way.
        Assert.Contains("scoop.ps1", Assert.Throws<InvalidOperationException>(() => ElevatedProgram.Checked(elevated, new Locator { ScriptProblem = "scoop.ps1 is installed in a folder that any program on this PC could change." }, @"C:\Users\me\scoop\apps\scoop\current\bin\scoop.ps1")).Message);
    }

    // Real folders on this PC: the system folder is Windows' own, the temporary folder is the person's.
    [Fact]
    public void OnThisPcTheSystemFolderMayRunAndTheTemporaryFolderMayNot()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(ElevatedProgram.Problem(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        var planted = Path.Combine(Path.GetTempPath(), "tw-planted-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.WriteAllText(planted, "");
        try { Assert.Contains("will not run it with administrator rights", ElevatedProgram.Problem(planted)); }
        finally { File.Delete(planted); }
    }

    [Fact]
    public void WinGetsAliasResolvesToItsPackageUnderWindowsAppsWhenItIsInstalled()
    {
        if (!OperatingSystem.IsWindows()) return;
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(alias) || !File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint)) return;
        var target = AppExecutionAlias.Target(alias);
        Assert.NotNull(target);
        Assert.EndsWith("winget.exe", target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\WindowsApps\Microsoft.DesktopAppInstaller_", target, StringComparison.OrdinalIgnoreCase);
        var (file, problem) = ElevatedProgram.Resolve(alias);
        Assert.Equal(target, file, ignoreCase: true);
        Assert.Null(problem);
    }

    private sealed class Locator : IExecutableLocator
    {
        public string? Problem { get; init; }
        public string? ScriptProblem { get; init; }
        public string? Resolved { get; init; }
        public string? Find(ManagerKind manager) => "x.exe";
        public string PowerShell => "powershell.exe";
        public (string FileName, string? Problem) Elevated(string fileName) => fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? (fileName, ScriptProblem) : (Resolved ?? fileName, Problem);
    }
}
