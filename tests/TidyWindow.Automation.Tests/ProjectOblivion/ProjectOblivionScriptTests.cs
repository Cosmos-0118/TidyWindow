using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public async Task ArtifactDiscoveryTreatsDecoyDataFoldersAsHeuristics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? tempRoot = null;
        try
        {
            tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ProjectOblivionScriptTests", "Discovery", Guid.NewGuid().ToString("N"))).FullName;
            var programData = Directory.CreateDirectory(Path.Combine(tempRoot, "ProgramData")).FullName;
            var localAppData = Directory.CreateDirectory(Path.Combine(tempRoot, "LocalAppData")).FullName;
            var appData = Directory.CreateDirectory(Path.Combine(tempRoot, "AppDataRoaming")).FullName;
            var programFiles = Directory.CreateDirectory(Path.Combine(tempRoot, "ProgramFiles")).FullName;
            var programFilesX86 = Directory.CreateDirectory(Path.Combine(tempRoot, "ProgramFilesX86")).FullName;
            var windowsRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "Windows")).FullName;
            var installRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "InstallRoot")).FullName;

            var sanitizedName = "SharedTools";
            Directory.CreateDirectory(Path.Combine(programData, sanitizedName));
            Directory.CreateDirectory(Path.Combine(localAppData, sanitizedName));
            Directory.CreateDirectory(Path.Combine(appData, sanitizedName));

            var appId = $"oblivion-decoy-{Guid.NewGuid():N}";
            var inventoryPayload = new
            {
                generatedAt = DateTimeOffset.UtcNow.ToString("o"),
                warnings = Array.Empty<string>(),
                apps = new[]
                {
                    new
                    {
                        appId,
                        name = "Shared Tools",
                        version = "1.0",
                        uninstallCommand = "cmd.exe /c exit /b 0",
                        quietUninstallCommand = string.Empty,
                        installRoot,
                        artifactHints = Array.Empty<string>(),
                        registry = (object?)null,
                        serviceHints = Array.Empty<string>(),
                        processHints = Array.Empty<string>(),
                        packageFamilyName = (string?)null,
                        tags = Array.Empty<string>()
                    }
                }
            };

            var inventoryPath = WriteInventoryPayload(Path.Combine(tempRoot, "decoy-inventory.json"), inventoryPayload);
            var scriptPath = ResolveRepositoryPath("automation", "scripts", "oblivion-artifact-discovery.ps1");

            var environment = new Dictionary<string, string?>
            {
                ["ProgramData"] = programData,
                ["LOCALAPPDATA"] = localAppData,
                ["APPDATA"] = appData,
                ["ProgramFiles"] = programFiles,
                ["ProgramFiles(x86)"] = programFilesX86,
                ["SystemRoot"] = windowsRoot,
                ["WINDIR"] = windowsRoot
            };

            var result = await RunPowerShellAsync(
                scriptPath,
                new[]
                {
                    "-AppId", appId,
                    "-InventoryPath", inventoryPath,
                    "-MaxProgramFilesMatches", "0"
                },
                environment);

            if (result.ExitCode != 0)
            {
                _output.WriteLine(result.StdOut);
                _output.WriteLine(result.StdErr);
            }

            Assert.Equal(0, result.ExitCode);

            var artifactsEvent = ExtractStructuredEvents(result.StdOut)
                .FirstOrDefault(evt => string.Equals(evt["type"]?.GetValue<string>(), "artifacts", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(artifactsEvent);

            var items = artifactsEvent!["items"]?.AsArray();
            Assert.NotNull(items);

            var dataFolderArtifacts = items!
                .Select(node => node?.AsObject())
                .Where(node => string.Equals(node?["metadata"]?["reason"]?.GetValue<string>(), "DataFolder", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.NotEmpty(dataFolderArtifacts);

            foreach (var artifact in dataFolderArtifacts)
            {
                Assert.False(artifact!["defaultSelected"]?.GetValue<bool>() ?? true);
                Assert.Equal("heuristic", artifact["metadata"]?["confidence"]?.GetValue<string>());
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
            {
                TryDelete(tempRoot);
            }
        }
    }

    [Fact]
    public async Task ForceCleanupFailsWhenSelectionTimesOut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? tempRoot = null;
        try
        {
            tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ProjectOblivionScriptTests", "Timeout", Guid.NewGuid().ToString("N"))).FullName;
            var appId = $"oblivion-timeout-{Guid.NewGuid():N}";
            var inventoryPath = WriteMinimalInventory(tempRoot, appId);
            var selectionPath = Path.Combine(tempRoot, "selection.json");
            var scriptPath = ResolveRepositoryPath("automation", "scripts", "oblivion-force-cleanup.ps1");

            var result = await RunPowerShellAsync(
                scriptPath,
                new[]
                {
                    "-AppId", appId,
                    "-InventoryPath", inventoryPath,
                    "-SelectionPath", selectionPath,
                    "-WaitForSelection",
                    "-SelectionTimeoutSeconds", "2",
                    "-DryRun"
                });

            if (result.ExitCode == 0)
            {
                _output.WriteLine(result.StdOut);
                _output.WriteLine(result.StdErr);
            }

            Assert.NotEqual(0, result.ExitCode);
            var combined = (result.StdOut ?? string.Empty) + (result.StdErr ?? string.Empty);
            Assert.Contains("Selection file not provided before timeout.", combined, StringComparison.OrdinalIgnoreCase);

            var awaitingEvent = ExtractStructuredEvents(result.StdOut)
                .Any(evt => string.Equals(evt["type"]?.GetValue<string>(), "awaitingSelection", StringComparison.OrdinalIgnoreCase));

            if (!awaitingEvent)
            {
                _output.WriteLine(result.StdOut);
            }

            Assert.True(awaitingEvent);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
            {
                TryDelete(tempRoot);
            }
        }
    }

    [Fact]
    public async Task ForceCleanupRejectsCorruptSelectionPayload()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? tempRoot = null;
        try
        {
            tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ProjectOblivionScriptTests", "CorruptSelection", Guid.NewGuid().ToString("N"))).FullName;
            var appId = $"oblivion-corrupt-{Guid.NewGuid():N}";
            var inventoryPath = WriteMinimalInventory(tempRoot, appId);
            var selectionPath = Path.Combine(tempRoot, "selection.json");

            var invalidSelection = "{\n  \"selectedIds\": [],\n  \"removeAll\": true\n}";
            WriteSelectionFile(selectionPath, invalidSelection);

            var scriptPath = ResolveRepositoryPath("automation", "scripts", "oblivion-force-cleanup.ps1");
            var result = await RunPowerShellAsync(
                scriptPath,
                new[]
                {
                    "-AppId", appId,
                    "-InventoryPath", inventoryPath,
                    "-SelectionPath", selectionPath,
                    "-DryRun"
                });

            if (result.ExitCode == 0)
            {
                _output.WriteLine(result.StdOut);
                _output.WriteLine(result.StdErr);
            }

            Assert.NotEqual(0, result.ExitCode);
            var combined = (result.StdOut ?? string.Empty) + (result.StdErr ?? string.Empty);
            Assert.Contains("unsupported property 'removeAll'", combined, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
            {
                TryDelete(tempRoot);
            }
        }
    }

    [Fact]
    public async Task ForceCleanupResumesWithExistingSelection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? tempRoot = null;
        try
        {
            tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ProjectOblivionScriptTests", "Resume", Guid.NewGuid().ToString("N"))).FullName;
            var appId = $"oblivion-resume-{Guid.NewGuid():N}";
            var inventoryPath = WriteMinimalInventory(tempRoot, appId);
            var scriptPath = ResolveRepositoryPath("automation", "scripts", "oblivion-force-cleanup.ps1");

            var discoveryResult = await RunPowerShellAsync(
                scriptPath,
                new[]
                {
                    "-AppId", appId,
                    "-InventoryPath", inventoryPath,
                    "-AutoSelectAll",
                    "-DryRun"
                });

            if (discoveryResult.ExitCode != 0)
            {
                _output.WriteLine(discoveryResult.StdOut);
                _output.WriteLine(discoveryResult.StdErr);
            }

            Assert.Equal(0, discoveryResult.ExitCode);

            var artifactsEvent = ExtractStructuredEvents(discoveryResult.StdOut)
                .FirstOrDefault(evt => string.Equals(evt["type"]?.GetValue<string>(), "artifacts", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(artifactsEvent);

            var artifactIds = artifactsEvent!["items"]?.AsArray()
                .Select(node => node?["id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            Assert.NotNull(artifactIds);
            Assert.True(artifactIds!.Count > 0, "Expected the inventory to produce at least one artifact.");

            var selectionPath = Path.Combine(tempRoot, "resume-selection.json");
            var primarySelection = new[] { artifactIds[0]! };
            var deselections = artifactIds.Count > 1 ? new[] { artifactIds[1]! } : Array.Empty<string>();
            WriteSelectionPayload(selectionPath, primarySelection, deselections);

            var resumeResult = await RunPowerShellAsync(
                scriptPath,
                new[]
                {
                    "-AppId", appId,
                    "-InventoryPath", inventoryPath,
                    "-SelectionPath", selectionPath,
                    "-WaitForSelection",
                    "-SelectionTimeoutSeconds", "5",
                    "-DryRun"
                });

            if (resumeResult.ExitCode != 0)
            {
                _output.WriteLine(resumeResult.StdOut);
                _output.WriteLine(resumeResult.StdErr);
            }

            Assert.Equal(0, resumeResult.ExitCode);

            var selectionEvent = ExtractStructuredEvents(resumeResult.StdOut)
                .FirstOrDefault(evt => string.Equals(evt["type"]?.GetValue<string>(), "selection", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(selectionEvent);
            Assert.Equal(primarySelection.Length, selectionEvent!["selected"]?.GetValue<int>());
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
            {
                TryDelete(tempRoot);
            }
        }
    }

    private async Task<ScriptResult> RunPowerShellAsync(string scriptPath, IEnumerable<string>? arguments = null, IDictionary<string, string?>? environment = null)
    {
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

        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }
        }

        if (environment is not null)
        {
            foreach (var kvp in environment)
            {
                psi.Environment[kvp.Key] = kvp.Value ?? string.Empty;
            }
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start pwsh process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ScriptResult(process.ExitCode, stdout, stderr);
    }

    private static IEnumerable<JsonObject> ExtractStructuredEvents(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            yield break;
        }

        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                continue;
            }

            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(trimmed)?.AsObject();
            }
            catch
            {
                continue;
            }

            if (parsed is not null && parsed["type"] is not null)
            {
                yield return parsed;
            }
        }
    }

    private static string ResolveRepositoryPath(params string[] segments)
    {
        var parts = new List<string>
        {
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."
        };
        parts.AddRange(segments);
        return Path.GetFullPath(Path.Combine(parts.ToArray()));
    }

    private static string WriteInventoryPayload(string path, object payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }

    private static void WriteSelectionPayload(string selectionPath, IEnumerable<string> selectedIds, IEnumerable<string>? deselectedIds = null)
    {
        if (selectedIds is null)
        {
            throw new ArgumentNullException(nameof(selectedIds));
        }

        var normalizedSelected = selectedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (normalizedSelected.Length == 0)
        {
            throw new ArgumentException("Selection must contain at least one artifact identifier.", nameof(selectedIds));
        }

        var payload = new Dictionary<string, object?>
        {
            ["selectedIds"] = normalizedSelected
        };

        if (deselectedIds is not null)
        {
            payload["deselectedIds"] = deselectedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        }

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        WriteSelectionFile(selectionPath, json);
    }

    private static void WriteSelectionFile(string selectionPath, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(selectionPath)!);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(selectionPath, json, encoding);
        var hash = SHA256.HashData(File.ReadAllBytes(selectionPath));
        File.WriteAllText(selectionPath + ".sha256", Convert.ToHexString(hash));
    }

    private readonly record struct ScriptResult(int ExitCode, string StdOut, string StdErr);

    private static string WriteMinimalInventory(string root, string appId)
    {
        var installRoot = Path.Combine(root, "install-root");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "stub.txt"), "placeholder");

        var hintFolder = Path.Combine(root, "hint-folder");
        Directory.CreateDirectory(hintFolder);

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

        return WriteInventoryPayload(Path.Combine(root, "inventory.json"), payload);
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
