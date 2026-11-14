using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>What happens after WinGet fails to uninstall an app: each failure gets the remedy it points to, a working uninstall is left alone.</summary>
[SupportedOSPlatform("windows")]
public sealed class UninstallLadderTests : IDisposable
{
    private const string Code = "{3B7E1A52-6C0D-4E8F-9A21-5D4C7B8E9F01}";
    private const string MachineArp = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Code;
    private const string Name = "PuTTY release 0.83 (64-bit)";
    private static readonly ManagedPackage Putty = new(ManagerKind.Winget, "PuTTY.PuTTY", Name, "0.83.0.0", Source: "winget");
    private static readonly ManagedPackage Arp = new(ManagerKind.Winget, @"ARP\Machine\X64\" + Code, Name, "0.83.0.0");

    private readonly RepairSandbox sandbox = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-uninstall-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { sandbox.Dispose(); try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static ManagerStatus Ready() => new(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Winget), ManagerHealth.Ready, "1", "winget.exe", "Ready");

    private sealed class Locator : IExecutableLocator
    {
        /// <summary>Why WinGet's program may not run as administrator; null lets it (the real rules are ElevatedProgramTests' subject).</summary>
        public string? Problem { get; init; }
        public string? Find(ManagerKind manager) => "winget.exe";
        public string PowerShell => "powershell.exe";
        public (string FileName, string? Problem) Elevated(string fileName) => (fileName, Problem);
    }

    private sealed class Host : IUninstallHost
    {
        public bool IsAdministrator { get; set; }
        public ClosedPrograms Closing = ClosedPrograms.None;
        public List<string> ClosedIn = [];
        public string Head = "";
        /// <summary>Why an uninstaller may not run as administrator, by its path; null lets it.</summary>
        public Func<string, string?> Problem = _ => null;
        public Task<ClosedPrograms> CloseProgramsInAsync(string folder, CancellationToken token) { ClosedIn.Add(folder); return Task.FromResult(Closing); }
        public string ReadHead(string path, int bytes) => Head;
        public (string FileName, string? Problem) Elevated(string fileName) => (fileName, Problem(fileName));
    }

    /// <summary>A WinGet that lists the app until <see cref="Listed"/> says otherwise, and answers each uninstall from a script.</summary>
    private sealed class Winget(Func<CommandSpec, int, CommandResult> uninstall) : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        public Func<bool> Listed = () => true;
        /// <summary>What the list shows while the app is listed, when it is more than the one row <see cref="Table"/> makes.</summary>
        public Func<string>? Rows;
        public string Id = "PuTTY.PuTTY", Source = "winget";
        public Action<CommandSpec>? OnRun;
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (command.Arguments.Count > 0 && command.Arguments[0] == "list")
                return Task.FromResult(new CommandResult(0, Listed() ? Rows?.Invoke() ?? Table(Id, Source) : "Name  Id  Version  Source\n" + new string('-', 40) + "\n", ""));
            var attempt = Commands.Count(x => x.Arguments.Count > 0 && x.Arguments[0] == "uninstall" || x.FileName != "winget.exe" && x.Arguments.Count > 0 && x.Arguments[0] != "list");
            OnRun?.Invoke(command);
            return Task.FromResult(uninstall(command, attempt));
        }
        public IEnumerable<CommandSpec> Uninstalls => Commands.Where(x => x.Arguments.Count > 0 && x.Arguments[0] == "uninstall");
    }

    private static string Table(string id, string source)
    {
        string[] cells = [Name, id, "0.83.0.0", source];
        int[] widths = [Name.Length + 2, Math.Max(id.Length, 2) + 2, 10, 8];
        string Row(string[] row) => string.Concat(row.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd();
        return Row(["Name", "Id", "Version", "Source"]) + "\n" + new string('-', widths.Sum()) + "\n" + Row(cells) + "\n";
    }

    private static CommandResult Fail(int inner) => new(unchecked((int)0x8A150006), $"Found {Name} [PuTTY.PuTTY]\nStarting package uninstall...\nUninstall failed with exit code: {inner}", "");

    private InstallationService Service(Winget runner, IInstallSystem pc, Host host, UninstallRecovery? recovery = null, RepairService? repair = null, Locator? locator = null)
    {
        recovery ??= new UninstallRecovery(pc, host, runner, repair) { Delay = (_, _) => Task.CompletedTask };
        locator ??= new Locator();
        return new InstallationService(new PackageService(runner, locator), runner, locator, null, recovery) { VerifyDelays = [], LateVerifyDelays = [] };
    }

    private static UninstallEntry Entry(string key, string uninstall = "", string quiet = "", string location = "", bool msi = true, string bundle = "") =>
        new(key, Name, "0.83.0.0", "Simon Tatham", uninstall, quiet, location, "", msi, false, false, false, bundle);

    private static FakePc Pc(params UninstallEntry[] entries) => new() { Programs = [.. entries], State = _ => 5 };

    // ---- Reading what failed ----

    [Fact]
    public void AnUninstallFailureCodeIsReadAndExplained()
    {
        var result = Fail(1605);
        Assert.Equal(1605, InstallFailure.InnerExitCode(result));
        var reason = InstallFailure.Explain(result, uninstall: true);
        Assert.Contains("uninstaller started but reported error 1605", reason);
        Assert.Contains("no longer has this app registered", reason);
        Assert.Null(InstallFailure.InnerExitCode(new(1, "nothing useful", "")));
    }

    [Theory]
    [InlineData(@"ARP\Machine\ARM64\Some App", true)]
    [InlineData(@"ARP\Machine\X64\Vendor's App [beta]; x", true)]
    [InlineData(@"ARP\User\X86\Приложение 1.0", true)]
    [InlineData(@"ARP\Machine\X64\{3B7E1A52-6C0D-4E8F-9A21-5D4C7B8E9F01}", true)]
    [InlineData(@"ARP\Machine\X64\Vendor App…", false)]
    [InlineData(@"ARP\Machine\X99\App", false)]
    [InlineData(@"ARP\Machine\X64\a|calc", false)]
    [InlineData(@"ARP\Machine\X64\a""b", false)]
    public void RegistryStyleIdentifiersKeepTheirRealCharactersButNothingThatReachesAShell(string id, bool allowed) =>
        Assert.Equal(allowed, PackageOutputParser.IsSafeInstalledId(id));

    [Fact]
    public void TheCommandCanLeaveOutTheSource()
    {
        var service = new InstallationService(new PackageService(new Winget((_, _) => new(0, "", "")), new Locator()), new Winget((_, _) => new(0, "", "")), new Locator());
        Assert.Contains("--source", service.ActionCommand(Ready(), Putty, PackageAction.Uninstall).Arguments);
        Assert.DoesNotContain("--source", service.ActionCommand(Ready(), Putty, PackageAction.Uninstall, withoutSource: true).Arguments);
    }

    // ---- The ladder, one failure at a time ----

    [Fact]
    public async Task AnUninstallThatWorksTheFirstTimeRunsOneCommandAndNothingElse()
    {
        var uninstalled = false;
        var runner = new Winget((_, _) => { uninstalled = true; return new(0, "Successfully uninstalled", ""); }) { Listed = () => !uninstalled };
        var host = new Host();
        var result = await Service(runner, Pc(), host).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Single(runner.Uninstalls);
        Assert.False(runner.Uninstalls.Single().Elevate);
        Assert.DoesNotContain("What TidyWindow tried", result.Detail);
        Assert.Empty(host.ClosedIn);
    }

    [Fact]
    public async Task AnInstallerThatIsBusyIsWaitedForAndThenTheUninstallRunsAgain()
    {
        var busy = 3;
        var pc = new BusyPc(Pc(), () => busy-- > 0);
        var uninstalled = false;
        var runner = new Winget((_, attempt) => attempt == 1 ? Fail(1618) : Done(ref uninstalled)) { Listed = () => !uninstalled };
        var waits = 0;
        var recovery = new UninstallRecovery(pc, new Host(), runner) { Delay = (_, _) => { waits++; return Task.CompletedTask; } };
        var result = await Service(runner, pc, new Host(), recovery).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.True(waits >= 1);
        Assert.Equal(2, runner.Uninstalls.Count());
        Assert.Contains("Waited for the other Windows Installer task", result.Detail);
    }

    private static CommandResult Done(ref bool flag) { flag = true; return new(0, "Successfully uninstalled", ""); }

    private sealed class BusyPc(FakePc inner, Func<bool> busy) : IInstallSystem
    {
        public IReadOnlyList<UninstallEntry> Entries() => inner.Entries();
        public IReadOnlyList<MsiProduct> Products() => inner.Products();
        public int ProductState(string code) => inner.ProductState(code);
        public bool FileExists(string path) => inner.FileExists(path);
        public bool FolderExists(string path) => inner.FolderExists(path);
        public bool DriveAvailable(string path) => true;
        public string? FindOnPath(string fileName) => null;
        public string Expand(string text) => text;
        public bool InstallLockExists() => false;
        public bool InstallerBusy() => busy();
        public IReadOnlyList<RunningInstaller> Installers() => [];
        public IReadOnlyList<string> PendingRestart() => [];
        public bool EndProcess(int id) => false;
    }

    [Fact]
    public async Task APerMachineUninstallThatFailsSilentlyIsRunAgainAsAdministratorOnce()
    {
        var uninstalled = false;
        var runner = new Winget((command, _) => command.Elevate ? Done(ref uninstalled) : Fail(1603)) { Listed = () => !uninstalled };
        var result = await Service(runner, Pc(Entry(MachineArp, $"MsiExec.exe /X{Code}")), new Host()).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Equal([false, true], runner.Uninstalls.Select(x => x.Elevate));
    }

    [Fact]
    public async Task APerUserAppIsNotAskedForAdministratorPermissionJustBecauseItFailed()
    {
        const string userKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Chat";
        var runner = new Winget((_, _) => Fail(1603)) { Id = @"ARP\User\X64\Chat" };
        var app = new ManagedPackage(ManagerKind.Winget, @"ARP\User\X64\Chat", Name, "0.83.0.0");
        var result = await Service(runner, Pc(Entry(userKey, "", "", msi: false)), new Host()).RunAsync(Ready(), app, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(runner.Uninstalls, x => x.Elevate);
    }

    [Fact]
    public async Task AnAdministratorAppIsNeverAskedToElevateAgain()
    {
        var runner = new Winget((_, _) => Fail(1603));
        var result = await Service(runner, Pc(Entry(MachineArp, $"MsiExec.exe /X{Code}")), new Host { IsAdministrator = true }).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(runner.Uninstalls, x => x.Elevate);
    }

    [Fact]
    public async Task AnAppMatchedToWinGetsCatalogIsRetriedAgainstWhatIsInstalled()
    {
        var uninstalled = false;
        var runner = new Winget((command, _) => command.Arguments.Contains("--source") ? new(unchecked((int)0x8A150014), "No installed package found matching input criteria.", "") : Done(ref uninstalled)) { Listed = () => !uninstalled };
        var result = await Service(runner, Pc(), new Host { IsAdministrator = true }).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Contains("--source", runner.Uninstalls.First().Arguments);
        Assert.DoesNotContain("--source", runner.Uninstalls.Last().Arguments);
    }

    [Fact]
    public async Task ABackgroundHelperLockingTheFolderIsClosedAndTheUninstallRunsAgain()
    {
        var uninstalled = false;
        var closed = false;
        var runner = new Winget((_, _) => closed ? Done(ref uninstalled) : new(unchecked((int)0x8A150101), "The application is currently running.", "")) { Listed = () => !uninstalled };
        var host = new Host { IsAdministrator = true };
        var pc = Pc(Entry(MachineArp, $"MsiExec.exe /X{Code}", location: @"C:\Program Files\PuTTY"));
        pc.Folders.Add(@"C:\Program Files\PuTTY");
        host.Closing = new(["pageant.exe"], []);
        runner.OnRun = _ => closed = host.ClosedIn.Count > 0;
        var result = await Service(runner, pc, host).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Equal([@"C:\Program Files\PuTTY"], host.ClosedIn);
        Assert.Contains("Closed pageant.exe", result.Detail);
    }

    [Fact]
    public async Task AWindowThatStaysOpenIsLeftForThePersonToClose()
    {
        var runner = new Winget((_, _) => new(unchecked((int)0x8A150101), "The application is currently running.", ""));
        var host = new Host { IsAdministrator = true, Closing = new([], ["putty.exe"]) };
        var pc = Pc(Entry(MachineArp, $"MsiExec.exe /X{Code}", location: @"C:\Program Files\PuTTY"));
        pc.Folders.Add(@"C:\Program Files\PuTTY");
        var result = await Service(runner, pc, host).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("putty.exe is still open. Close it yourself", result.Detail);
    }

    [Fact]
    public async Task AWindowLeftOpenStopsTheLadderInsteadOfRunningTheUninstallerIntoItAgain()
    {
        // Not an administrator and a per-machine app: every other remedy (elevation, no source, the app's own uninstaller) would otherwise follow.
        var runner = new Winget((_, _) => new(unchecked((int)0x8A150101), "The application is currently running.", ""));
        var host = new Host { Closing = new([], ["putty.exe"]) };
        var pc = Pc(Entry(MachineArp, "", $"\"C:\\Program Files\\PuTTY\\unins000.exe\" /SILENT", location: @"C:\Program Files\PuTTY", msi: false));
        pc.Folders.Add(@"C:\Program Files\PuTTY"); pc.Files.Add(@"C:\Program Files\PuTTY\unins000.exe");
        var result = await Service(runner, pc, host).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Single(runner.Commands, x => x.Arguments[0] != "list");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AnInnoOrNsisUninstallerThatExitsWithAPlainCodeStillHasItsHelpersClosed(int plain)
    {
        var closed = false;
        var uninstalled = false;
        var runner = new Winget((_, _) => closed ? Done(ref uninstalled) : Fail(plain)) { Listed = () => !uninstalled };
        var host = new Host { IsAdministrator = true, Closing = new(["tray.exe"], []) };
        var pc = Pc(Entry(MachineArp, "", location: @"C:\Program Files\Tool", msi: false));
        pc.Folders.Add(@"C:\Program Files\Tool");
        runner.OnRun = _ => closed = host.ClosedIn.Count > 0;
        var result = await Service(runner, pc, host).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Contains("Closed tray.exe", result.Detail);
    }

    [Fact]
    public async Task AnElevatedRetryThatShowsNoOutputKeepsTheReasonTheFirstRunGave()
    {
        // No entry is known, so the ladder has nothing but WinGet to run again: the elevated run, then the same without a source.
        var recovery = new UninstallRecovery(Pc(), new Host(), new Winget((_, _) => Nothing));
        var outcome = await recovery.RecoverAsync(Putty, Fail(1603), variant => Task.FromResult(variant.Elevated ? new CommandResult(1, "", "") : Fail(1603)), null, default);
        Assert.Equal(1, outcome.Result.ExitCode);
        Assert.Equal(1603, InstallFailure.InnerExitCode(outcome.Result));
        Assert.NotNull(InstallFailure.Explain(outcome.Result, uninstall: true));
    }

    [Fact]
    public async Task AnElevatedRetryThatWorksIsNotShownTheOldFailureText()
    {
        var recovery = new UninstallRecovery(Pc(), new Host(), new Winget((_, _) => Nothing));
        var outcome = await recovery.RecoverAsync(Putty, Fail(1603), variant => Task.FromResult(variant.Elevated ? new CommandResult(0, "", "") : Fail(1603)), null, default);
        Assert.Equal(0, outcome.Result.ExitCode);
        Assert.Null(InstallFailure.InnerExitCode(outcome.Result));
    }

    [Fact]
    public async Task TheAppsOwnUninstallersExitCodeIsExplainedLikeWinGetsInnerCode()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool_is1";
        const string exe = @"C:\Program Files\Tool\unins000.exe";
        var runner = new Winget((command, _) => command.FileName == exe ? new(2, "", "") : Fail(2)) { Id = @"ARP\Machine\X64\Tool_is1" };
        var pc = Pc(Entry(key, $"\"{exe}\"", msi: false));
        pc.Files.Add(exe);
        var app = new ManagedPackage(ManagerKind.Winget, @"ARP\Machine\X64\Tool_is1", Name, "0.83.0.0");
        var result = await Service(runner, pc, new Host { IsAdministrator = true }).RunAsync(Ready(), app, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("uninstaller started but reported error 2", result.Detail);
    }

    [Fact]
    public void ANameWinGetShortensOrANewerVersionSpellingStillFindsTheEntry()
    {
        var python = new UninstallEntry(@"HKLM\x\Py", "Python 3.13.9 (64-bit)", "3.13.9150.0", "PSF", "", "", "", "", false, false, false, false);
        var msys = new UninstallEntry(@"HKLM\x\Ms", "MSYS2", "20260101", "", "", "", "", "", false, false, false, false);
        var other = new UninstallEntry(@"HKLM\x\Py2", "Python 3.12.1 (64-bit)", "3.12.1150.0", "PSF", "", "", "", "", false, false, false, false);
        var recovery = new UninstallRecovery(Pc(python, msys, other), new Host(), new Winget((_, _) => Nothing));
        Assert.Equal(@"HKLM\x\Ms", recovery.FindEntry(new(ManagerKind.Winget, "MSYS2.MSYS2", "MSYS2 Installer", "20260101"))!.Key);
        Assert.Equal(@"HKLM\x\Py", recovery.FindEntry(new(ManagerKind.Winget, "Python.Python.3.13", "Python 3.13", "3.13.9150"))!.Key);
        // The same start of the name with a different version is another program.
        Assert.Null(recovery.FindEntry(new(ManagerKind.Winget, "Python.Python.3.11", "Python 3.11", "3.11.0")));
        Assert.Equal(@"HKLM\x\Ms", recovery.FindEntry(new(ManagerKind.Winget, "MSYS2.MSYS2", "MSYS2", "20260101.0"))!.Key);
    }

    [Fact]
    public async Task WhenWinGetCannotUninstallTheAppsOwnSilentUninstallerIsRunDirectly()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool_is1";
        const string exe = @"C:\Program Files\Tool\unins000.exe";
        var uninstalled = false;
        var runner = new Winget((command, _) => command.FileName == exe ? Done(ref uninstalled) : Fail(2)) { Listed = () => !uninstalled, Id = @"ARP\Machine\X64\Tool_is1" };
        var pc = Pc(Entry(key, $"\"{exe}\"", msi: false));
        pc.Files.Add(exe);
        var app = new ManagedPackage(ManagerKind.Winget, @"ARP\Machine\X64\Tool_is1", Name, "0.83.0.0");
        var result = await Service(runner, pc, new Host { IsAdministrator = true }).RunAsync(Ready(), app, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        var direct = Assert.Single(runner.Commands, x => x.FileName == exe);
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], direct.Arguments);
        Assert.Contains("Inno Setup", result.Detail);
    }

    [Fact]
    public async Task AnUninstallerWhoseEngineIsNotRecognisedIsNotGuessedAt()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Odd";
        const string exe = @"C:\Odd\remove.exe";
        var runner = new Winget((_, _) => Fail(2)) { Id = @"ARP\Machine\X64\Odd" };
        var pc = Pc(Entry(key, $"\"{exe}\"", msi: false));
        pc.Files.Add(exe);
        var app = new ManagedPackage(ManagerKind.Winget, @"ARP\Machine\X64\Odd", Name, "0.83.0.0");
        var result = await Service(runner, pc, new Host { IsAdministrator = true }).RunAsync(Ready(), app, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(runner.Commands, x => x.FileName == exe);
        Assert.Contains("did not guess", result.Detail);
    }

    // ---- Administrator rights are given only to programs nobody but an administrator can replace ----

    [Fact]
    public async Task AnElevatedRetryIsRefusedWhenWinGetsProgramLivesWhereAnyoneCouldReplaceIt()
    {
        var runner = new Winget((_, _) => Fail(1603));
        const string reason = @"winget.exe is installed in a folder that any program on this PC could change (C:\Users\me\x), so TidyWindow will not run it with administrator rights.";
        var result = await Service(runner, Pc(Entry(MachineArp, $"MsiExec.exe /X{Code}")), new Host(), locator: new Locator { Problem = reason }).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        // WinGet is never run as administrator; the ladder's last step still runs Windows Installer itself, which lives in the system folder.
        Assert.DoesNotContain(runner.Uninstalls, x => x.Elevate);
        Assert.Contains(runner.Commands, x => x.Elevate && x.FileName.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Did not run the uninstall again as administrator: " + reason, result.Detail);
        // The first failure is still what the explanation reads, and the other remedies were still tried.
        Assert.Contains("reported error 1603", result.Detail);
        Assert.Contains(runner.Uninstalls, x => !x.Arguments.Contains("--source"));
    }

    [Fact]
    public async Task AnUninstallerRegisteredForTheWholePcIsNotRunFromAFolderAnyoneCouldChange()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool_is1";
        const string exe = @"C:\Users\Public\Tool\unins000.exe";
        const string reason = @"unins000.exe is installed in a folder that any program on this PC could change (C:\Users\Public\Tool), so TidyWindow will not run it with administrator rights.";
        var runner = new Winget((command, _) => command.FileName == exe ? new(0, "", "") : Fail(2)) { Id = @"ARP\Machine\X64\Tool_is1" };
        var pc = Pc(Entry(key, $"\"{exe}\"", msi: false));
        pc.Files.Add(exe);
        // Whether TidyWindow has administrator rights itself or would ask for them, the program would run with them either way.
        foreach (var administrator in new[] { true, false })
        {
            var host = new Host { IsAdministrator = administrator, Problem = path => path == exe ? reason : null };
            var app = new ManagedPackage(ManagerKind.Winget, @"ARP\Machine\X64\Tool_is1", Name, "0.83.0.0");
            var result = await Service(runner, pc, host).RunAsync(Ready(), app, PackageAction.Uninstall);
            Assert.Equal(ActionOutcome.Failed, result.Outcome);
            Assert.DoesNotContain(runner.Commands, x => x.FileName == exe);
            Assert.Contains($"Did not run its own uninstaller ({exe}): {reason}", result.Detail);
        }
    }

    [Fact]
    public async Task APerUserUninstallerRunsAsThePersonSoItsFolderIsNotJudged()
    {
        const string key = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool_is1";
        const string exe = @"C:\Users\me\AppData\Local\Tool\unins000.exe";
        var removed = false;
        var runner = new Winget((command, _) => command.FileName == exe ? Done(ref removed) : Fail(2)) { Id = @"ARP\User\X64\Tool_is1", Listed = () => !removed };
        var pc = Pc(Entry(key, $"\"{exe}\"", msi: false));
        pc.Files.Add(exe);
        var host = new Host { Problem = _ => "never asked" };
        var app = new ManagedPackage(ManagerKind.Winget, @"ARP\User\X64\Tool_is1", Name, "0.83.0.0");
        var result = await Service(runner, pc, host).RunAsync(Ready(), app, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        var direct = Assert.Single(runner.Commands, x => x.FileName == exe);
        Assert.False(direct.Elevate);
    }

    [Fact]
    public async Task AWholePcUninstallerIsRunFromThePathTheHostResolvedAndThePathIsShownFirst()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool_is1";
        const string exe = @"C:\Program Files\Tool\unins000.exe";
        var removed = false;
        var runner = new Winget((command, _) => command.FileName == exe ? Done(ref removed) : Fail(2)) { Id = @"ARP\Machine\X64\Tool_is1", Listed = () => !removed };
        var pc = Pc(Entry(key, $"\"{exe}\"", msi: false));
        pc.Files.Add(exe);
        var app = new ManagedPackage(ManagerKind.Winget, @"ARP\Machine\X64\Tool_is1", Name, "0.83.0.0");
        var result = await Service(runner, pc, new Host()).RunAsync(Ready(), app, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        var direct = Assert.Single(runner.Commands, x => x.FileName == exe);
        Assert.True(direct.Elevate);
        var lines = result.Detail.Split('\n');
        Assert.Contains(lines, x => x.Contains($"Running its own uninstaller directly (Inno Setup): {exe} /VERYSILENT", StringComparison.Ordinal));
        Assert.True(Array.FindIndex(lines, x => x.Contains("Running its own uninstaller", StringComparison.Ordinal)) < Array.FindIndex(lines, x => x.Contains("Its own uninstaller returned 0", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnEntryForAProgramWindowsInstallerNoLongerHasIsRemovedAndTheAppLeavesTheList()
    {
        // The PuTTY case: the list entry is all that is left, so every uninstall answers 1605.
        var key = BrokenPython.Arp(Code);
        sandbox.Put(key, ("DisplayName", Name), ("UninstallString", $"MsiExec.exe /X{Code}"), ("WindowsInstaller", 1));
        var pc = new FakePc { State = _ => InstallRepair.Unknown, Live = () => sandbox.Has(key) ? [Entry(key, $"MsiExec.exe /X{Code}")] : [] };
        var runner = new Winget((_, _) => Fail(1605)) { Listed = () => sandbox.Has(key), Id = @"ARP\User\X64\" + Code };
        var repair = new RepairService(pc, sandbox.User(), sandbox.Elevated(), _ => Task.CompletedTask, () => { }, new RepairHistory(folder));
        var app = new ManagedPackage(ManagerKind.Winget, @"ARP\User\X64\" + Code, Name, "0.83.0.0");

        var result = await Service(runner, pc, new Host { IsAdministrator = true }, repair: repair).RunAsync(Ready(), app, PackageAction.Uninstall);

        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.False(sandbox.Has(key));
        Assert.Contains("leftover entry", result.Detail);
        // A backup was kept, so Repair › Undo can put it back.
        Assert.NotEmpty(new RepairHistory(folder).All());
    }

    [Fact]
    public async Task AProgramThatIsStillOnThePcIsNeverUnregisteredJustBecauseItsUninstallFailed()
    {
        // Its uninstaller is gone, the folder is still there, and nothing can restore it: the entry must stay.
        const string key = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool";
        sandbox.Put(key, ("DisplayName", Name), ("UninstallString", @"""C:\Tool\unins000.exe"""), ("InstallLocation", @"C:\Tool"));
        var pc = new FakePc { State = _ => 5, Live = () => sandbox.Has(key) ? [Entry(key, @"""C:\Tool\unins000.exe""", location: @"C:\Tool", msi: false)] : [] };
        pc.Folders.Add(@"C:\Tool");
        var runner = new Winget((_, _) => Fail(2)) { Id = @"ARP\User\X64\Tool" };
        var repair = new RepairService(pc, sandbox.User(), sandbox.Elevated(), _ => Task.CompletedTask, () => { }, new RepairHistory(folder));
        var app = new ManagedPackage(ManagerKind.Winget, @"ARP\User\X64\Tool", Name, "0.83.0.0");

        var result = await Service(runner, pc, new Host { IsAdministrator = true }, repair: repair).RunAsync(Ready(), app, PackageAction.Uninstall);

        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.True(sandbox.Has(key));
        Assert.Contains("nothing was removed", result.Detail);
    }

    // ---- Running an app's own uninstaller ----

    private static NativeUninstall? Plan(UninstallEntry entry, FakePc pc, string head = "", Func<string, string>? machineExpand = null) => NativeUninstall.Plan(entry, pc, (_, _) => head, machineExpand);

    [Fact]
    public void AWholePcEntryIsExpandedWithThePcsVariablesNeverThePersons()
    {
        var pc = Pc(); pc.Files.Add(@"C:\Program Files\Tool\unins000.exe"); pc.Files.Add(@"C:\Users\me\AppData\Local\Tool\unins000.exe");
        static string Machine(string text) => text.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);
        var plan = Plan(Entry(@"HKLM\x\Tool_is1", @"""%ProgramFiles%\Tool\unins000.exe""", msi: false), pc, machineExpand: Machine);
        Assert.Equal(@"C:\Program Files\Tool\unins000.exe", plan!.FileName);
        // A variable only the person's session has (and any program running as the person can set) is not filled in for an entry registered for the whole PC.
        Assert.Null(Plan(Entry(@"HKLM\x\Tool_is1", @"""%LOCALAPPDATA%\Tool\unins000.exe""", msi: false), pc, machineExpand: Machine));
    }

    [Fact]
    public void MachineVariablesExpandNestedValuesAndLeaveUnknownOnesAlone()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SystemRoot"] = @"C:\Windows", ["TEMP"] = @"%SystemRoot%\TEMP" };
        Assert.Equal(@"C:\Windows\TEMP\x.exe", MachineEnvironment.Expand(@"%temp%\x.exe", variables));
        Assert.Equal(@"%LOCALAPPDATA%\x.exe", MachineEnvironment.Expand(@"%LOCALAPPDATA%\x.exe", variables));
        Assert.Equal(@"100% C:\Windows", MachineEnvironment.Expand(@"100% %SystemRoot%", variables));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windows, "notepad.exe"), MachineEnvironment.Expand(@"%SystemRoot%\notepad.exe"));
        Assert.Equal(@"%USERPROFILE%\x.exe", MachineEnvironment.Expand(@"%USERPROFILE%\x.exe"));
    }

    [Fact]
    public void AnMsiThatSaysInstallIsRemovedByItsCodeNotOpenedAgain()
    {
        var plan = Plan(Entry(MachineArp, $"MsiExec.exe /I{Code}"), Pc());
        Assert.EndsWith("msiexec.exe", plan!.FileName);
        Assert.Equal(["/x", Code, "/qn", "/norestart", "REBOOT=ReallySuppress"], plan.Arguments);
        Assert.True(plan.Machine);
    }

    [Fact]
    public void AnMsiWindowsInstallerHasForgottenHasNothingToRun() =>
        Assert.Null(Plan(Entry(MachineArp, $"MsiExec.exe /X{Code}"), new FakePc { State = _ => InstallRepair.Unknown }));

    [Fact]
    public void AQuietUninstallStringIsRunAsItIs()
    {
        var pc = Pc(); pc.Files.Add(@"C:\App\remove.exe");
        var plan = Plan(Entry(@"HKCU\x\App", @"""C:\App\remove.exe""", @"""C:\App\remove.exe"" --silent --keep ""my data""", msi: false), pc);
        Assert.Equal(@"C:\App\remove.exe", plan!.FileName);
        Assert.Equal(["--silent", "--keep", "my data"], plan.Arguments);
        Assert.False(plan.Machine);
    }

    [Fact]
    public void BurnSquirrelAndNsisUninstallersGetTheirOwnSilentSwitches()
    {
        var pc = Pc();
        pc.Files.Add(@"C:\ProgramData\Package Cache\{B}\setup.exe"); pc.Files.Add(@"C:\Users\x\AppData\Local\Chat\Update.exe"); pc.Files.Add(@"C:\Nsis\uninst.exe"); pc.Files.Add(@"C:\Other\uninst.exe");

        var burn = Plan(Entry(@"HKLM\x\B", @"""C:\ProgramData\Package Cache\{B}\setup.exe"" /uninstall", msi: false, bundle: "{B}"), pc);
        Assert.Equal(["/uninstall", "/quiet", "/norestart"], burn!.Arguments);

        var squirrel = Plan(Entry(@"HKCU\x\Chat", @"""C:\Users\x\AppData\Local\Chat\Update.exe"" --uninstall", msi: false), pc);
        Assert.Equal(["--uninstall", "-s"], squirrel!.Arguments);

        var nsis = Plan(Entry(@"HKLM\x\N", @"""C:\Nsis\uninst.exe""", msi: false), pc, head: "...Nullsoft Install System v3.08...");
        Assert.Equal(["/S"], nsis!.Arguments);

        // The same file name without the NSIS signature is not run with guessed switches.
        Assert.Null(Plan(Entry(@"HKLM\x\O", @"""C:\Other\uninst.exe""", msi: false), pc, head: "something else"));
    }

    [Fact]
    public void ACommandWhoseProgramIsMissingHasNothingToRun() =>
        Assert.Null(Plan(Entry(@"HKLM\x\M", @"""C:\Gone\unins000.exe""", msi: false), Pc()));

    // ---- Where the ladder looks for the app's entry ----

    [Fact]
    public void AnEntryIsFoundByItsRegistryKeyOrByItsNameWhenWinGetMatchedItToTheCatalog()
    {
        var pc = Pc(Entry(MachineArp, $"MsiExec.exe /X{Code}"));
        var recovery = new UninstallRecovery(pc, new Host(), new Winget((_, _) => new(0, "", "")));
        Assert.Equal(MachineArp, recovery.FindEntry(Arp)!.Key);
        Assert.Equal(MachineArp, recovery.FindEntry(Putty)!.Key);
        Assert.Null(recovery.FindEntry(Putty with { Name = "Something else" }));
        Assert.Null(recovery.FindEntry(new(ManagerKind.Winget, @"MSIX\Some.App_1.0_x64__abc", Name, "1")));
        Assert.Null(recovery.FindEntry(new(ManagerKind.Winget, @"ARP\User\X64\" + Code, Name, "1")));
    }

    [Fact]
    public void FoldersThatHoldWindowsOrOtherProgramsAreNeverClosedOutOf()
    {
        foreach (var folder in new[] { @"C:\", Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppContext.BaseDirectory.TrimEnd('\\'),
            Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\'))! })
            Assert.True(WindowsUninstallHost.IsProtected(folder.TrimEnd('\\') is { Length: 0 } ? @"C:" : folder.TrimEnd('\\')), folder);
        Assert.False(WindowsUninstallHost.IsProtected(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PuTTY")));
    }

    // ---- Scoop, Chocolatey and Store apps ----

    private static readonly CommandResult Nothing = new(1, "", "failed");

    [Fact]
    public async Task AScoopAppThatCannotBeDeletedHasItsRunningProgramsClosedAndIsTriedAgain()
    {
        var pc = Pc(); pc.Folders.Add(@"C:\scoop\apps\git");
        var host = new Host { Closing = new(["git-bash.exe"], []) };
        var recovery = new UninstallRecovery(pc, host, new Winget((_, _) => Nothing)) { ManagedFolder = _ => @"C:\scoop\apps\git" };
        var reruns = 0;
        var outcome = await recovery.RecoverAsync(new(ManagerKind.Scoop, "git", "git", "2.0"), Nothing, _ => { reruns++; return Task.FromResult(new CommandResult(0, "", "")); }, null, default);
        Assert.Equal(0, outcome.Result.ExitCode);
        Assert.Equal(1, reruns);
        Assert.Equal([@"C:\scoop\apps\git"], host.ClosedIn);
    }

    private sealed class ChocoRunner(Func<int, CommandResult> uninstall, Func<bool> listed) : ICommandRunner
    {
        public int Uninstalls;
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) =>
            Task.FromResult(command.Arguments[0] == "list" ? new CommandResult(0, listed() ? "tool|1.0" : "other|1", "") : uninstall(++Uninstalls));
    }

    [Fact]
    public async Task AManagerThatExitsZeroWhileTheAppIsStillInstalledIsTreatedAsFailedAndRecovered()
    {
        // Scoop prints "ERROR … still running" and exits 0.
        var gone = false;
        var runner = new ChocoRunner(attempt => { if (attempt >= 2) gone = true; return attempt == 1 ? new(0, "ERROR The following instances of \"tool\" are still running.", "") : new(0, "ok", ""); }, () => !gone);
        var pc = Pc(); pc.Folders.Add(@"C:\tools\tool");
        var host = new Host { Closing = new(["tool.exe"], []) };
        var recovery = new UninstallRecovery(pc, host, runner) { ManagedFolder = _ => @"C:\tools\tool" };
        var service = new InstallationService(new PackageService(runner, new Locator()), runner, new Locator(), null, recovery) { VerifyDelays = [], LateVerifyDelays = [] };
        var status = new ManagerStatus(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Chocolatey), ManagerHealth.Ready, "1", "choco.exe", "Ready");

        var result = await service.RunAsync(status, new(ManagerKind.Chocolatey, "tool", "tool", "1.0"), PackageAction.Uninstall);

        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Equal(2, runner.Uninstalls);
        Assert.Contains("Closed tool.exe", result.Detail);
        Assert.Contains("reported success, but the app is still installed", result.Detail);
    }

    [Fact]
    public async Task AManagerThatReallyUninstalledIsNotRetried()
    {
        var gone = false;
        var runner = new ChocoRunner(_ => { gone = true; return new(0, "ok", ""); }, () => !gone);
        var host = new Host { Closing = new(["tool.exe"], []) };
        var recovery = new UninstallRecovery(Pc(), host, runner) { ManagedFolder = _ => @"C:\tools\tool" };
        var service = new InstallationService(new PackageService(runner, new Locator()), runner, new Locator(), null, recovery) { VerifyDelays = [], LateVerifyDelays = [] };
        var status = new ManagerStatus(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Chocolatey), ManagerHealth.Ready, "1", "choco.exe", "Ready");
        var result = await service.RunAsync(status, new(ManagerKind.Chocolatey, "tool", "tool", "1.0"), PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Equal(1, runner.Uninstalls);
        Assert.Empty(host.ClosedIn);
    }

    [Fact]
    public async Task AChocolateyUninstallBlockedByAnotherInstallerWaitsAndIsTriedAgain()
    {
        var busy = 2;
        var pc = new BusyPc(Pc(), () => busy-- > 0);
        var recovery = new UninstallRecovery(pc, new Host(), new Winget((_, _) => Nothing)) { Delay = (_, _) => Task.CompletedTask };
        var blocked = new CommandResult(1, "Running [msiexec] was not successful. Exit code was '1618'.", "");
        var reruns = 0;
        var outcome = await recovery.RecoverAsync(new(ManagerKind.Chocolatey, "git", "git", "2.0"), blocked, _ => { reruns++; return Task.FromResult(new CommandResult(0, "", "")); }, null, default);
        Assert.Equal(0, outcome.Result.ExitCode);
        Assert.Equal(1, reruns);
    }

    [Fact]
    public async Task ChocolateyAndScoopAppsAreNeverMatchedToAWindowsEntryByName()
    {
        // A package id such as "putty" is not a display name: running some other program's uninstaller would be worse than failing.
        var runner = new Winget((_, _) => Nothing);
        var pc = Pc(Entry(MachineArp, $"MsiExec.exe /X{Code}"));
        var recovery = new UninstallRecovery(pc, new Host { IsAdministrator = true }, runner);
        var outcome = await recovery.RecoverAsync(new(ManagerKind.Chocolatey, "putty", Name, "0.83.0.0"), Nothing, _ => Task.FromResult(Nothing), null, default);
        Assert.Equal(1, outcome.Result.ExitCode);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task AStoreAppWinGetCannotRemoveIsRemovedWithWindowsPowerShell()
    {
        const string id = @"MSIX\Vendor.App_1.2.3.0_x64__abc123";
        var removed = false;
        var runner = new Winget((command, _) =>
        {
            if (command.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)) { removed = true; return new(0, "", ""); }
            return new(unchecked((int)0x8A150006), "Uninstall failed with exit code: 5", "");
        }) { Id = id, Listed = () => !removed };
        var pc = Pc();
        var recovery = new UninstallRecovery(pc, new Host { IsAdministrator = true }, runner) { WindowsPowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" };
        var service = new InstallationService(new PackageService(runner, new Locator()), runner, new Locator(), null, recovery) { VerifyDelays = [], LateVerifyDelays = [] };
        var result = await service.RunAsync(Ready(), new(ManagerKind.Winget, id, Name, "0.83.0.0"), PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        var script = Assert.Single(runner.Commands, x => x.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
        var text = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(script.Arguments[^1]));
        Assert.Contains("Remove-AppxPackage -Package 'Vendor.App_1.2.3.0_x64__abc123'", text);
        Assert.Contains("Remove-AppxPackage", result.Detail);
    }

    [Theory]
    [InlineData(@"MSIX\Vendor.App_1.0_x64__abc", "Vendor.App_1.0_x64__abc")]
    [InlineData(@"MSIX\Vendor'App_1.0", null)]
    [InlineData(@"ARP\Machine\X64\App", null)]
    public void OnlyARealPackageFullNameIsEverPutInTheScript(string id, string? expected) =>
        Assert.Equal(expected, UninstallRecovery.StoreFullName(new(ManagerKind.Winget, id, "x", "1")));

    [Fact]
    public void ARepairKeepsAProgramsRecordWhenAskedToOnlyRestoreIt() => Assert.True(new RepairOptions(RestoreOnly: true).RestoreOnly);

    // ---- Several installed versions, and an uninstaller that only works with its window ----

    private static CommandResult Several() => new(unchecked((int)0x8A150010), "Multiple versions of this package are installed. Either refine the search, pass the `--version` argument to select one, or pass the `--all-versions` flag to uninstall all of them.", "");

    private static string TwoCopies(string first, string firstVersion, string second, string secondVersion)
    {
        string[][] rows = [["Name", "Id", "Version", "Source"], [first, "PuTTY.PuTTY", firstVersion, "winget"], [second, "PuTTY.PuTTY", secondVersion, "winget"]];
        int[] widths = [Math.Max(first.Length, second.Length) + 2, 16, 12, 8];
        string Row(string[] cells) => string.Concat(cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
        return Row(rows[0]) + "\n" + new string('-', widths.Sum()) + "\n" + Row(rows[1]) + "\n" + Row(rows[2]) + "\n";
    }

    private static UninstallEntry Listing(string key, string name, bool windowsInstaller = true) =>
        new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + key, name, "8.0.34", "Microsoft", "", "", "", "", windowsInstaller, false, false, false);

    [Fact]
    public async Task WithoutAChoiceSeveralCopiesAreNeverGuessedAt()
    {
        var runner = new Winget((_, _) => Several()) { Rows = () => TwoCopies("PuTTY (x64)", "0.83", "PuTTY (x86)", "0.83") };
        var host = new Host();
        var result = await Service(runner, Pc(), host).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Single(runner.Uninstalls);
        Assert.DoesNotContain("--all-versions", runner.Uninstalls.Single().Arguments);
        Assert.DoesNotContain("--version", runner.Uninstalls.Single().Arguments);
        Assert.Contains("installed more than once", result.Detail);
        Assert.Empty(host.ClosedIn);
    }

    [Fact]
    public async Task RemovingAllCopiesSaysAllVersionsFromTheFirstCommand()
    {
        var uninstalled = false;
        var runner = new Winget((command, _) => { uninstalled = command.Arguments.Contains("--all-versions"); return uninstalled ? new(0, "Successfully uninstalled", "") : Several(); })
        { Listed = () => !uninstalled, Rows = () => TwoCopies("PuTTY (x64)", "0.83", "PuTTY (x86)", "0.83") };
        var result = await Service(runner, Pc(), new Host()).RunAsync(Ready(), Putty with { AllCopies = true }, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Single(runner.Uninstalls);
        Assert.Contains("All 2 installed copies", result.Detail);
    }

    [Fact]
    public async Task OneCopyWithItsOwnVersionIsChosenByVersionAndTheOtherIsKept()
    {
        var uninstalled = false;
        var runner = new Winget((command, _) => { uninstalled = command.Arguments.Contains("--version"); return uninstalled ? new(0, "Successfully uninstalled", "") : Several(); })
        { Rows = () => uninstalled ? Table(Putty.Id, "winget").Replace(Name, "PuTTY 2").Replace("0.83.0.0", "2.0") : TwoCopies("PuTTY 1", "1.0", "PuTTY 2", "2.0") };
        var result = await Service(runner, Pc(), new Host()).RunAsync(Ready(), Putty with { OnlyCopy = new("PuTTY 1", "1.0") }, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        var command = runner.Uninstalls.Single();
        Assert.Equal("1.0", command.Arguments[command.Arguments.ToList().IndexOf("--version") + 1]);
        Assert.Contains("Only “PuTTY 1” was removed", result.Detail);
    }

    [Fact]
    public async Task OneOfTwoCopiesWithTheSameVersionIsChosenByItsListEntry()
    {
        var uninstalled = false;
        var runner = new Winget((command, _) => { uninstalled = command.Arguments.Contains("--product-code"); return uninstalled ? new(0, "Successfully uninstalled", "") : Several(); })
        { Rows = () => uninstalled ? Table(Putty.Id, "winget").Replace(Name, "PuTTY (x64)") : TwoCopies("PuTTY (x64)", "0.83.0.0", "PuTTY (x86)", "0.83.0.0") };
        // The 32-bit copy is listed twice, as a bundle and as the package inside it: WinGet lists the bundle.
        var pc = Pc(Listing("{AAAA0000-0000-0000-0000-00000000000X}", "PuTTY (x64)"), Listing("{BBBB-package}", "PuTTY (x86)"), Listing("{CCCC-bundle}", "PuTTY (x86)", windowsInstaller: false));
        var result = await Service(runner, pc, new Host()).RunAsync(Ready(), Putty with { OnlyCopy = new("PuTTY (x86)", "0.83.0.0") }, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        var command = runner.Uninstalls.Single();
        Assert.Equal("{CCCC-bundle}", command.Arguments[command.Arguments.ToList().IndexOf("--product-code") + 1]);
        Assert.DoesNotContain("--id", command.Arguments);
    }

    [Fact]
    public async Task ACopyThatCannotBeToldApartIsNotGuessedAt()
    {
        var runner = new Winget((_, _) => Several()) { Rows = () => TwoCopies("PuTTY (x64)", "0.83.0.0", "PuTTY (x86)", "0.83.0.0") };
        var result = await Service(runner, Pc(), new Host()).RunAsync(Ready(), Putty with { OnlyCopy = new("PuTTY (x86)", "0.83.0.0") }, PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Empty(runner.Uninstalls);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("< 1.2")]
    [InlineData("1.2…")]
    [InlineData("")]
    public void AVersionWinGetCouldNotReadIsNeverPassedAsAnArgument(string version)
    {
        Assert.Null(ManagedPackage.UsableVersion(version));
        var service = new InstallationService(new PackageService(new Winget((_, _) => new(0, "", "")), new Locator()), new Winget((_, _) => new(0, "", "")), new Locator());
        Assert.DoesNotContain("--version", service.ActionCommand(Ready(), Putty, PackageAction.Uninstall, version: version).Arguments);
    }

    [Fact]
    public async Task AnUninstallerThatFailsForAnUnknownReasonIsOpenedWithItsOwnWindowExactlyOnce()
    {
        var runner = new Winget((command, _) => command.Arguments.Contains("--interactive") ? new(0, "Successfully uninstalled", "") : Fail(4362266));
        var result = await Service(runner, Pc(), new Host()).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.Single(runner.Uninstalls, x => x.Arguments.Contains("--interactive"));
        Assert.DoesNotContain("--disable-interactivity", runner.Uninstalls.Last().Arguments);
        Assert.Contains("own window", result.Detail);
        var failed = Fail(4362266);
        Assert.Contains("own uninstaller failing", InstallFailure.Explain(failed, uninstall: true));
    }

    [Fact]
    public async Task ACancelledUninstallerIsNeverReopened()
    {
        var runner = new Winget((_, _) => Fail(1602));
        await Service(runner, Pc(), new Host()).RunAsync(Ready(), Putty, PackageAction.Uninstall);
        Assert.DoesNotContain(runner.Uninstalls, x => x.Arguments.Contains("--interactive"));
    }
}
