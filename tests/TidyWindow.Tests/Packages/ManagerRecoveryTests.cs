using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

public sealed class ManagerRecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WorkingManagerIsVerifiedWithoutRunningAnyRepair()
    {
        using var fixture = new Fixture();
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.True(result.Success); Assert.False(result.Changed); Assert.Null(result.LogDirectory);
        Assert.Equal(0, fixture.Runner.Repairs);
    }

    [Fact]
    public async Task MissingManagerInstallsThenVerifiesRealState()
    {
        using var fixture = new Fixture();
        fixture.Locator.Path = null;
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.True(result.Success); Assert.True(result.Changed);
        Assert.Equal(ManagerHealth.Ready, result.Status.Health);
        Assert.Equal(1, fixture.Runner.Repairs);
    }

    [Fact]
    public async Task BrokenManagerPreservesPackageRecordsBeforeRepair()
    {
        using var fixture = new Fixture();
        fixture.Runner.Broken = true;
        var records = Path.Combine(fixture.Root, "lib", "git"); Directory.CreateDirectory(records);
        var record = Path.Combine(records, "git.nuspec"); File.WriteAllText(record, "<package>existing record</package>");
        var config = Path.Combine(fixture.Root, "config"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "chocolatey.config"), "<chocolatey />");
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.True(result.Success);
        Assert.Equal(File.ReadAllText(record), File.ReadAllText(Path.Combine(result.LogDirectory!, "records", "git", "git.nuspec")));
        Assert.Equal("<chocolatey />", File.ReadAllText(Path.Combine(result.LogDirectory!, "config-chocolatey.config")));
    }

    [Fact]
    public async Task SuccessfulExitAloneCannotReportSuccessfulRepair()
    {
        using var fixture = new Fixture(); fixture.Runner.Broken = true; fixture.Runner.RepairWorks = false;
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success); Assert.Equal(ManagerHealth.Unavailable, result.Status.Health);
    }

    [Fact]
    public async Task OfflineSourceDoesNotTriggerReinstallation()
    {
        using var fixture = new Fixture(); fixture.Runner.SourceError = "Network unreachable";
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success); Assert.Equal(ManagerHealth.ConnectionIssue, result.Status.Health);
        Assert.Equal(0, fixture.Runner.Repairs);
    }

    [Fact]
    public async Task PolicyRestrictionDoesNotTriggerReinstallation()
    {
        using var fixture = new Fixture(); fixture.Runner.SourceError = "Operation blocked by Group Policy";
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success); Assert.Equal(ManagerHealth.Restricted, result.Status.Health);
        Assert.Equal(0, fixture.Runner.Repairs);
    }

    [Fact]
    public async Task CorruptInventoryCannotBeReportedHealthy()
    {
        using var fixture = new Fixture(); fixture.Runner.Inventory = "Unrecognized output from a broken configuration";
        var health = new ManagerHealthService(fixture.Packages, fixture.Runner, fixture.Locator, (_, _) => null);
        var result = await health.CheckAsync(ManagerKind.Chocolatey);
        Assert.Equal(ManagerHealth.Unavailable, result.Health);
        Assert.Equal(ManagerProblem.Configuration, result.Problem);
    }

    [Fact]
    public async Task TheScriptsOwnErrorIsReportedFromTheJobFolderOrTheProtectedOne()
    {
        using var fixture = new Fixture(); fixture.Runner.Broken = true; fixture.Runner.RepairWorks = false;
        // An elevated run that could not write to the job folder leaves its result in its own folder under ProgramData, named after the job.
        fixture.Runner.OnRepair = job => { var mine = Path.Combine(fixture.ProtectedRoot, "Recovery", Path.GetFileName(job)); Directory.CreateDirectory(mine); File.WriteAllText(Path.Combine(mine, "result.json"), """{"exitCode":1,"error":"choco.exe does not carry a valid signature from Chocolatey Software (NotSigned), so it was not run."}"""); return new(1, "", ""); };
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success);
        Assert.Contains("valid signature", result.Status.Detail);
    }

    [Fact]
    public async Task AnElevatedRepairThatOutlivesItsTimeKeepsTheGateUntilItEnds()
    {
        using var fixture = new Fixture(); fixture.Runner.Broken = true;
        string? job = null;
        fixture.Runner.OnRepair = folder => { job = folder; throw new InvalidOperationException("The installer is still running in its own window. Check that window before trying again."); };
        var recovery = new ManagerRecoveryService(fixture.Packages, fixture.Runner, fixture.Locator, _ => new(fixture.Root, ""), Path.Combine(fixture.DirectoryPath, "jobs"), (_, _) => null, fixture.ProtectedRoot)
        { LingerPoll = TimeSpan.FromMilliseconds(50), LingerLimit = TimeSpan.FromSeconds(20) };
        var result = await recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success);
        Assert.Contains("still running", result.Message);
        Assert.NotNull(recovery.Lingering);
        // Nothing else may start beside the administrator window: not another repair, not an install, not a bucket.
        Assert.False(await fixture.Packages.OperationGate.WaitAsync(0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverAsync(ManagerKind.Chocolatey));
        File.WriteAllText(Path.Combine(job!, "result.json"), """{"exitCode":0,"error":null}""");
        await recovery.Lingering.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(await fixture.Packages.OperationGate.WaitAsync(0));
        fixture.Packages.OperationGate.Release();
    }

    [Theory]
    [InlineData(ManagerKind.Winget, ManagerProblem.Executable, false)]
    [InlineData(ManagerKind.Winget, ManagerProblem.Source, true)]
    [InlineData(ManagerKind.Chocolatey, ManagerProblem.NotInstalled, true)]
    [InlineData(ManagerKind.Scoop, ManagerProblem.Executable, false)]
    public async Task RecoveryScriptsParseAndElevateOnlyRequiredOperations(ManagerKind kind, ManagerProblem problem, bool elevate)
    {
        using var fixture = new Fixture();
        var definition = ManagerDefinition.All.Single(x => x.Kind == kind);
        var command = fixture.Recovery.CreateCommand(new(definition, ManagerHealth.Unavailable, "", "manager.exe", "broken", problem), new(fixture.Root, ""), fixture.Root);
        Assert.Equal(elevate, command.Elevate);
        // Only hosts that administrators alone can change: an elevated run executes whatever file the path names.
        Assert.Equal(kind == ManagerKind.Scoop ? new ExecutableLocator().PowerShell : ElevatedProgram.WindowsPowerShell, command.FileName);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1]));
        Assert.DoesNotContain("Remove-Item", script);
        // Nothing fetched at run time is unpinned, and nothing is unpacked by a module that could be planted in a profile.
        Assert.DoesNotContain("Expand-Archive", script);
        Assert.DoesNotContain("master", script);
        Assert.DoesNotContain("Invoke-WebRequest", script);
        // A run with administrator rights loads modules only from Windows' own folders and works in an administrator-only folder; the hardening comes before anything else runs.
        Assert.Equal(elevate, script.Contains("$PSModuleAutoLoadingPreference = 'None'"));
        if (elevate) Assert.True(script.IndexOf("$PSModuleAutoLoadingPreference = 'None'", StringComparison.Ordinal) < script.IndexOf("Add-Type", StringComparison.Ordinal));
        if (elevate) Assert.Contains("Use-ProtectedWorkspace", script);
        if (kind != ManagerKind.Winget) Assert.Contains("[IO.Compression.ZipFile]::ExtractToDirectory", script);
        output.WriteLine($"{kind}/{problem}: command line {command.Arguments.Sum(x => x.Length + 3)} characters");
        Assert.True(command.Arguments.Sum(x => x.Length + 3) < 30000, "Recovery command exceeds the Windows command-line limit.");
        var scriptFile = Path.Combine(fixture.Root, "parse-target.ps1"); File.WriteAllText(scriptFile, script);
        var parseScript = "$tokens=$null; $errors=$null; [Management.Automation.Language.Parser]::ParseFile('" + scriptFile.Replace("'", "''") + "',[ref]$tokens,[ref]$errors) | Out-Null; if($errors.Count) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }";
        var parsed = await new ProcessRunner().RunAsync(new(new ExecutableLocator().PowerShell,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(parseScript))], TimeSpan.FromSeconds(10)));
        Assert.True(parsed.ExitCode == 0, parsed.Error);
    }

    [Fact]
    public void RootDirectoryCannotBeUsedAsAnAutomaticRepairTarget()
    {
        Assert.Throws<InvalidOperationException>(() => ManagerLocations.ValidateRoot(Path.GetPathRoot(Environment.SystemDirectory)!));
        Assert.Throws<InvalidOperationException>(() => ManagerLocations.ValidateRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.Throws<InvalidOperationException>(() => ManagerLocations.ValidateRoot("relative/path"));
    }

    // The scripts run for real here, in Windows PowerShell as in production, without administrator rights and with the download replaced by a stub
    // or a local server. The pinned downloads themselves are not fetched: tests must not depend on the network.
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task<CommandResult> RunScriptAsync(string host, string variables, string kind, string overrides)
    {
        var script = variables + ManagerRecoveryService.Resource("Common") + "\n" + overrides + "\n" + ManagerRecoveryService.Resource(kind) + "\n" + ManagerRecoveryService.Resource("Complete");
        return await new ProcessRunner().RunAsync(new(host, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(90)));
    }

    private static string Variables(string root, string job, string problem, string host, string configuration = "") =>
        $"$root={Literal(root)}; $configuration={Literal(configuration)}; $job={Literal(job)}; $problem={Literal(problem)}; $executable=''; $powershell={Literal(host)};\n";

    private static string ResultError(string job) =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(job, "result.json"))).RootElement.GetProperty("error").GetString() ?? "";

    [Fact]
    public async Task AnUnsignedChocolateyPackageIsNeverRun()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var job = Path.Combine(fixture.DirectoryPath, "job"); Directory.CreateDirectory(job);
        var marker = Path.Combine(fixture.DirectoryPath, "installer-ran.txt");
        // A package that looks right in every way except that nobody signed it: its installer would leave a marker if it ran.
        var staging = Path.Combine(fixture.DirectoryPath, "staging"); Directory.CreateDirectory(Path.Combine(staging, "tools", "chocolateyInstall"));
        File.WriteAllText(Path.Combine(staging, "tools", "chocolateyInstall.ps1"), $"Set-Content -LiteralPath '{marker}' -Value ran");
        File.WriteAllText(Path.Combine(staging, "tools", "chocolateysetup.psm1"), "function Initialize-Chocolatey {}");
        File.WriteAllBytes(Path.Combine(staging, "tools", "chocolateyInstall", "choco.exe"), [0x4D, 0x5A, 0, 0]);
        var fake = Path.Combine(fixture.DirectoryPath, "fake.nupkg"); ZipFile.CreateFromDirectory(staging, fake);
        var stub = $"function Download-Official([string]$uri, [string]$destination, [string]$sha256) {{ Copy-Item -LiteralPath {Literal(fake)} -Destination $destination }}";
        var result = await RunScriptAsync(ElevatedProgram.WindowsPowerShell, Variables(fixture.Root, job, "Executable", ElevatedProgram.WindowsPowerShell), "Chocolatey", stub);
        output.WriteLine(result.Output + result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("signature", ResultError(job));
        Assert.False(File.Exists(marker), "The unsigned installer ran.");
        Assert.False(File.Exists(Path.Combine(fixture.Root, "bin", "choco.exe")));
        Assert.True(File.Exists(Path.Combine(job, "operation.log")));
    }

    [Fact]
    public async Task ChocolateysOwnSignedFilesPassTheSignatureCheck()
    {
        var choco = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "choco.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(choco)) return;
        using var fixture = new Fixture();
        var job = Path.Combine(fixture.DirectoryPath, "job"); Directory.CreateDirectory(job);
        var script = Variables(fixture.Root, job, "Executable", ElevatedProgram.WindowsPowerShell) + ManagerRecoveryService.Resource("Common") + $"\nAssert-ChocolateySigned {Literal(choco)}\n" + ManagerRecoveryService.Resource("Complete");
        var result = await new ProcessRunner().RunAsync(new(ElevatedProgram.WindowsPowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(60)));
        Assert.True(result.ExitCode == 0, result.Output + result.Error + ResultError(job));
    }

    [Fact]
    public async Task DownloadsFollowRedirectsOnlyToOfficialHostsAndKeepOnlyTheExpectedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var job = Path.Combine(fixture.DirectoryPath, "job"); Directory.CreateDirectory(job);
        var payload = Encoding.ASCII.GetBytes("hello from the official source");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                var path = context.Request.Url!.AbsolutePath;
                if (path == "/good") { context.Response.StatusCode = 302; context.Response.RedirectLocation = $"http://127.0.0.1:{port}/file"; }
                else if (path == "/elsewhere") { context.Response.StatusCode = 302; context.Response.RedirectLocation = $"http://localhost:{port}/file"; }
                else if (path == "/file") { context.Response.StatusCode = 200; await context.Response.OutputStream.WriteAsync(payload); }
                else context.Response.StatusCode = 404;
                context.Response.Close();
            }
        });
        try
        {
            // The test's own server stands in for the official hosts; the rule itself (every hop checked, bytes checked) is what runs.
            var overrides = "function Test-OfficialAddress([uri]$uri) { return $uri.Host -eq '127.0.0.1' }";
            var body = $$"""
                Download-Official 'http://127.0.0.1:{{port}}/good' (Join-Path $job 'a.bin') '{{hash}}'
                $caught = ''
                try { Download-Official 'http://127.0.0.1:{{port}}/elsewhere' (Join-Path $job 'b.bin') '{{hash}}' } catch { $caught = $_.Exception.Message }
                if ($caught -notlike 'Unexpected download address*') { throw "A redirect to another host was followed: $caught" }
                $caught = ''
                try { Download-Official 'http://127.0.0.1:{{port}}/file' (Join-Path $job 'c.bin') '{{new string('0', 64)}}' } catch { $caught = $_.Exception.Message }
                if ($caught -notlike '*expects*') { throw "Unexpected bytes were accepted: $caught" }
                """;
            var script = Variables(fixture.Root, job, "Executable", ElevatedProgram.WindowsPowerShell) + ManagerRecoveryService.Resource("Common") + "\n" + overrides + "\n" + body + "\n" + ManagerRecoveryService.Resource("Complete");
            var result = await new ProcessRunner().RunAsync(new(ElevatedProgram.WindowsPowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(60)));
            Assert.True(result.ExitCode == 0, result.Output + result.Error + ResultError(job));
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(job, "a.bin")));
            Assert.False(File.Exists(Path.Combine(job, "b.bin")));
        }
        finally { listener.Stop(); listener.Close(); await serving.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }); }
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    [Fact]
    public async Task ABrokenScoopCoreIsReplacedFromThePinnedArchiveAndEverythingElseIsKept()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var host = new ExecutableLocator().PowerShell;
        var root = Path.Combine(fixture.DirectoryPath, "scoop");
        var core = Path.Combine(root, "apps", "scoop", "current");
        Directory.CreateDirectory(Path.Combine(core, "bin")); File.WriteAllText(Path.Combine(core, "bin", "scoop.ps1"), "throw 'Deliberately broken core'");
        var keep = Path.Combine(root, "buckets", "custom", "bucket", "keep.json"); Directory.CreateDirectory(Path.GetDirectoryName(keep)!); File.WriteAllText(keep, """{"version":"1"}""");
        var persisted = Path.Combine(root, "persist", "keep.txt"); Directory.CreateDirectory(Path.GetDirectoryName(persisted)!); File.WriteAllText(persisted, "user data must survive");
        // A stand-in for the release archive, with the top folder the real one has.
        var staging = Path.Combine(fixture.DirectoryPath, "staging", "Scoop-0.6.0", "bin"); Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "scoop.ps1"), "Write-Output 'Current Scoop version:'; Write-Output 'v0.6.0 - Released at 2026-01-01'");
        var fake = Path.Combine(fixture.DirectoryPath, "fake.zip"); ZipFile.CreateFromDirectory(Path.Combine(fixture.DirectoryPath, "staging"), fake);
        var stub = $"function Download-Official([string]$uri, [string]$destination, [string]$sha256) {{ if ($uri -notlike 'https://github.com/ScoopInstaller/Scoop/archive/refs/tags/v*.zip') {{ throw \"unexpected $uri\" }}; Copy-Item -LiteralPath {Literal(fake)} -Destination $destination }}";
        var job = Path.Combine(fixture.DirectoryPath, "job"); Directory.CreateDirectory(job);
        var result = await RunScriptAsync(host, Variables(root, job, "Executable", host), "Scoop", stub);
        Assert.True(result.ExitCode == 0, result.Output + result.Error + ResultError(job));
        Assert.Contains("v0.6.0", File.ReadAllText(Path.Combine(core, "bin", "scoop.ps1")));
        Assert.NotEmpty(Directory.EnumerateDirectories(Path.GetDirectoryName(core)!, "current.tidywindow-backup-*"));
        Assert.Contains("Join-Path", File.ReadAllText(Path.Combine(root, "shims", "scoop.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "shims", "scoop.cmd")));
        Assert.Equal("user data must survive", File.ReadAllText(persisted));
        Assert.True(File.Exists(keep));

        // A link where the core should be is not something the repair moves: whatever it points at is left alone.
        var linked = Path.Combine(fixture.DirectoryPath, "linked"); var elsewhere = Path.Combine(fixture.DirectoryPath, "elsewhere"); Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Path.Combine(linked, "apps", "scoop"));
        var link = await new ProcessRunner().RunAsync(new(host, ["-NoProfile", "-NonInteractive", "-Command", $"New-Item -ItemType Junction -Path {Literal(Path.Combine(linked, "apps", "scoop", "current"))} -Target {Literal(elsewhere)} | Out-Null"], TimeSpan.FromSeconds(30)));
        Assert.True(link.ExitCode == 0, link.Error);
        var job2 = Path.Combine(fixture.DirectoryPath, "job2"); Directory.CreateDirectory(job2);
        var refused = await RunScriptAsync(host, Variables(linked, job2, "Executable", host), "Scoop", stub);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("link", ResultError(job2));
        Assert.True(Directory.Exists(elsewhere));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        // A junction is removed on its own (not recursively), so that its target is never touched through it.
        Directory.Delete(Path.Combine(linked, "apps", "scoop", "current"));
    }

    [LiveFact]
    public async Task LiveManagersAreHealthyAndRecoveryIsIdempotentWhenExplicitlyEnabled()
    {
        var runner = new ProcessRunner(); var locator = new ExecutableLocator(); var packages = new PackageService(runner, locator);
        var health = new ManagerHealthService(packages, runner, locator);
        var recovery = new ManagerRecoveryService(packages, runner, locator);
        foreach (var manager in ManagerDefinition.All)
        {
            var status = await health.CheckAsync(manager.Kind, true);
            output.WriteLine($"{manager.Name}: {status.Health} {status.Version}. {status.Detail}");
            Assert.Equal(ManagerHealth.Ready, status.Health);
            var result = await recovery.RecoverAsync(manager.Kind);
            Assert.True(result.Success, result.Status.Detail);
            Assert.False(result.Changed);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "TidyWindow-Recovery-Tests", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(DirectoryPath, "manager");
        public string ProtectedRoot => Path.Combine(DirectoryPath, "protected");
        public Locator Locator { get; } = new();
        public FakeRunner Runner { get; }
        public PackageService Packages { get; }
        public ManagerRecoveryService Recovery { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Runner = new(Locator); Packages = new(Runner, Locator);
            // Tests work in temporary folders, which the elevated-program rule would refuse, so the rule is stood down here and tested on its own.
            Recovery = new(Packages, Runner, Locator, _ => new(Root, ""), Path.Combine(DirectoryPath, "jobs"), (_, _) => null, ProtectedRoot);
        }
        public void Dispose()
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TidyWindow-Recovery-Tests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(DirectoryPath).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(DirectoryPath, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private sealed class Locator : IExecutableLocator
    {
        public string? Path { get; set; } = "choco.exe";
        public string? Find(ManagerKind manager) => Path;
        public string PowerShell => new ExecutableLocator().PowerShell;
    }
    private sealed class FakeRunner(Locator locator) : ICommandRunner
    {
        public bool Broken { get; set; }
        public bool RepairWorks { get; set; } = true;
        public string Inventory { get; set; } = "git|2.50";
        public string? SourceError { get; set; }
        public int Repairs { get; private set; }
        /// <summary>Stands in for the script: receives the job folder (read from the encoded command) and answers for it.</summary>
        public Func<string, CommandResult>? OnRepair { get; set; }
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            if (command.Arguments.Contains("-EncodedCommand"))
            {
                Repairs++;
                if (OnRepair is not null)
                {
                    var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1]));
                    var job = System.Text.RegularExpressions.Regex.Match(script, @"\$job='([^']*)'").Groups[1].Value;
                    return Task.FromResult(OnRepair(job));
                }
                if (RepairWorks) { Broken = false; locator.Path = "choco.exe"; }
                return Task.FromResult(new CommandResult(0, "Recovery ran", ""));
            }
            var first = command.Arguments[0];
            return Task.FromResult(first switch
            {
                "--version" => Broken ? new(1, "", "Executable is damaged") : new CommandResult(0, "2.7.5", ""),
                "list" => new(0, Inventory, ""),
                "search" => SourceError is null ? new(0, "7zip|26.05", "") : new(1, "", SourceError),
                _ => throw new InvalidOperationException("Unexpected command")
            });
        }
    }
}
