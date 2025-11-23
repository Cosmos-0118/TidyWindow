using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace TidyWindow.Automation.Tests.PathPilot;

public sealed class PathPilotScriptTests
{
    private readonly ITestOutputHelper _output;

    public PathPilotScriptTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SwitchFailsWhenExecutablePathIsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PathPilotScriptTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var configPath = WriteRuntimeConfig(tempRoot);
            var missingExecutable = Path.Combine(tempRoot, "missing", "pwsh.exe");
            var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "automation", "scripts", "Get-PathPilotInventory.ps1"));

            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-ConfigPath");
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("-SwitchRuntimeId");
            psi.ArgumentList.Add("pathpilot-test");
            psi.ArgumentList.Add("-SwitchInstallPath");
            psi.ArgumentList.Add(missingExecutable);

            using var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start pwsh process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.NotEqual(0, process.ExitCode);
            var combined = string.Concat(stdout, Environment.NewLine, stderr);
            _output.WriteLine(combined);
            Assert.Contains("does not contain", combined, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    private static string WriteRuntimeConfig(string directory)
    {
        var configPath = Path.Combine(directory, "runtime-config.json");
        var payload = new PathPilotConfig
        {
            Runtimes = new[]
            {
                new PathPilotRuntimeConfig
                {
                    Id = "pathpilot-test",
                    DisplayName = "PathPilot Test",
                    ExecutableName = "pwsh.exe",
                    DesiredVersion = "7",
                    Discovery = new PathPilotDiscovery
                    {
                        WhereHints = new[] { "pwsh.exe" }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(configPath, json);
        return configPath;
    }

    private sealed class PathPilotConfig
    {
        public PathPilotRuntimeConfig[]? Runtimes { get; set; }
    }

    private sealed class PathPilotRuntimeConfig
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public string? ExecutableName { get; set; }

        public string? DesiredVersion { get; set; }

        public PathPilotDiscovery? Discovery { get; set; }
    }

    private sealed class PathPilotDiscovery
    {
        public string[]? WhereHints { get; set; }
    }
}
