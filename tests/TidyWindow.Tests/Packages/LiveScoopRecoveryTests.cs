using System.Text;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

public sealed class LiveScoopRecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RepairBrokenShimAndCoreInIsolatedRealScoopWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_RECOVERY_TESTS") != "1") return;
        var artifacts = Environment.GetEnvironmentVariable("TIDYWINDOW_TEST_ARTIFACTS") ?? throw new InvalidOperationException("Set TIDYWINDOW_TEST_ARTIFACTS to a test artifacts directory.");
        var fixture = Path.Combine(Path.GetFullPath(artifacts), Guid.NewGuid().ToString("N"));
        var root = Path.Combine(fixture, "isolated-scoop");
        var original = new ManagerLocations().Get(ManagerKind.Scoop).Root;
        var source = Path.Combine(original, "apps", "scoop", "current");
        var core = Path.Combine(root, "apps", "scoop", "current");
        foreach (var directory in new[] { "bin", "lib", "libexec", "supporting" })
            CopyTree(Path.Combine(source, directory), Path.Combine(core, directory));
        foreach (var file in new[] { "CHANGELOG.md", "buckets.json" }) File.Copy(Path.Combine(source, file), Path.Combine(core, file));
        var main = Path.Combine(root, "buckets", "main", "bucket"); Directory.CreateDirectory(main);
        File.Copy(Path.Combine(original, "buckets", "main", "bucket", "7zip.json"), Path.Combine(main, "7zip.json"));
        var custom = Path.Combine(root, "buckets", "custom", "bucket"); Directory.CreateDirectory(custom);
        var customFile = Path.Combine(custom, "keep.json"); File.WriteAllText(customFile, "{\"version\":\"1\",\"description\":\"Keep this custom bucket\"}");
        var persist = Path.Combine(root, "persist"); Directory.CreateDirectory(persist);
        var marker = Path.Combine(persist, "keep.txt"); File.WriteAllText(marker, "user data must survive");
        var configHome = Path.Combine(fixture, "config"); var config = Path.Combine(configHome, "scoop", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, System.Text.Json.JsonSerializer.Serialize(new { root_path = root, global_path = Path.Combine(fixture, "global") }));
        var initialConfig = File.ReadAllText(config);
        var shims = Path.Combine(root, "shims"); Directory.CreateDirectory(shims);
        var shim = Path.Combine(shims, "scoop.ps1"); File.WriteAllText(shim, "throw 'Deliberately broken isolated test shim'");
        var locator = new IsolatedLocator(shim);
        var runner = new IsolatedRunner(root, configHome, Path.Combine(fixture, "global"));
        var packages = new PackageService(runner, locator);
        var health = new ManagerHealthService(packages, runner, locator);
        var recovery = new ManagerRecoveryService(packages, runner, locator, _ => new(root, config), Path.Combine(fixture, "recovery"));
        var before = await health.CheckAsync(ManagerKind.Scoop, true);
        Assert.Equal(ManagerHealth.Unavailable, before.Health);
        var shimResult = await recovery.RecoverAsync(ManagerKind.Scoop);
        output.WriteLine($"Broken shim: {shimResult.Success}. {shimResult.Status.Detail}. Logs: {shimResult.LogDirectory}");
        Assert.True(shimResult.Success, shimResult.Status.Detail);
        Assert.Contains("Join-Path", File.ReadAllText(shim));
        Assert.Equal(initialConfig, File.ReadAllText(config));
        Assert.Equal("user data must survive", File.ReadAllText(marker));
        Assert.True(File.Exists(customFile));

        File.WriteAllText(Path.Combine(core, "bin", "scoop.ps1"), "throw 'Deliberately broken isolated test core'");
        var coreResult = await recovery.RecoverAsync(ManagerKind.Scoop);
        output.WriteLine($"Broken core: {coreResult.Success}. {coreResult.Status.Detail}. Logs: {coreResult.LogDirectory}");
        Assert.True(coreResult.Success, coreResult.Status.Detail);
        Assert.NotEmpty(Directory.EnumerateDirectories(Path.GetDirectoryName(core)!, "current.tidywindow-backup-*"));
        Assert.Equal(initialConfig, File.ReadAllText(config));
        Assert.Equal("user data must survive", File.ReadAllText(marker));
        Assert.True(File.Exists(customFile));
        var repeated = await recovery.RecoverAsync(ManagerKind.Scoop);
        Assert.True(repeated.Success); Assert.False(repeated.Changed);
        output.WriteLine("Fixture and repair evidence retained at " + fixture);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unexpected link in test source.");
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
    private sealed class IsolatedLocator(string shim) : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => File.Exists(shim) ? shim : null;
        public string PowerShell => new ExecutableLocator().PowerShell;
    }
    private sealed class IsolatedRunner(string root, string configHome, string global) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            if (command.Elevate) throw new InvalidOperationException("Isolated Scoop test must never elevate.");
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1]));
            var scoped = $"$env:SCOOP={Quote(root)}; $env:XDG_CONFIG_HOME={Quote(configHome)}; $env:SCOOP_GLOBAL={Quote(global)};\n" + script;
            var arguments = command.Arguments.ToArray(); arguments[^1] = Convert.ToBase64String(Encoding.Unicode.GetBytes(scoped));
            return new ProcessRunner().RunAsync(command with { Arguments = arguments }, cancellationToken);
        }
    }
}
