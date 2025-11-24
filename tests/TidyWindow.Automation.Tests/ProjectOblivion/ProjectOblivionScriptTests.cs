using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Automation.Tests.ProjectOblivion;

public sealed class ProjectOblivionScriptTests
{
    private readonly ITestOutputHelper _output;

    public ProjectOblivionScriptTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task DryRunEmitsSummaryWithRunLog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? runLogDirectory = null;
        string? tempRoot = null;

        try
        {
            tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ProjectOblivionScriptTests", Guid.NewGuid().ToString("N"))).FullName;
            var appId = $"oblivion-test-{Guid.NewGuid():N}";
            var inventoryPath = WriteMinimalInventory(tempRoot, appId);
            var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "automation", "scripts", "uninstall-app-deep.ps1"));

            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-AppId");
            psi.ArgumentList.Add(appId);
            psi.ArgumentList.Add("-InventoryPath");
            psi.ArgumentList.Add(inventoryPath);
            psi.ArgumentList.Add("-AutoSelectAll");
            psi.ArgumentList.Add("-DryRun");

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start pwsh process.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _output.WriteLine(stdout);
                _output.WriteLine(stderr);
            }

            Assert.Equal(0, process.ExitCode);

            var summaryLine = stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains("\"type\":\"summary\"", StringComparison.OrdinalIgnoreCase));

            Assert.False(string.IsNullOrWhiteSpace(summaryLine));

            var summary = JsonNode.Parse(summaryLine!)?.AsObject();
            Assert.NotNull(summary);

            var logPath = summary!["logPath"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(logPath));
            Assert.True(File.Exists(logPath));
            runLogDirectory = Path.GetDirectoryName(logPath);

            var logJson = await File.ReadAllTextAsync(logPath!);
            var logNode = JsonNode.Parse(logJson)?.AsObject();
            Assert.NotNull(logNode);
            Assert.Equal(appId, logNode!["appId"]?.GetValue<string>());
            Assert.Equal(summary!["logPath"]?.GetValue<string>(), logNode!["logPath"]?.GetValue<string>());
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
            {
                TryDelete(tempRoot);
            }

            if (!string.IsNullOrWhiteSpace(runLogDirectory) && Directory.Exists(runLogDirectory))
            {
                TryDelete(runLogDirectory);
            }
        }
    }

    private static string WriteMinimalInventory(string root, string appId)
    {
        var installRoot = Path.Combine(root, "install-root");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "stub.txt"), "placeholder");

        var hintFolder = Path.Combine(root, "hint-folder");
        Directory.CreateDirectory(hintFolder);

        var inventoryPath = Path.Combine(root, "inventory.json");
        var payload = new
        {
            apps = new[]
            {
                new
                {
                    appId,
                    name = "Project Oblivion Test App",
                    version = "1.0",
                    uninstallCommand = "cmd.exe /c exit /b 0",
                    quietUninstallCommand = "",
                    installRoot,
                    artifactHints = new[] { hintFolder },
                    registry = new { keyPath = "HKEY_CURRENT_USER\\Software\\ProjectOblivion\\Test" },
                    serviceHints = new[] { "OblivionTestService" }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(inventoryPath, json);
        return inventoryPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
