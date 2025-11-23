using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.PathPilot;

namespace TidyWindow.Core.Tests.PathPilot;

public sealed class PathPilotInventoryServiceTests : IDisposable
{
    private const string ScriptEnvVariable = "TIDYWINDOW_PATHPILOT_SCRIPT";
    private readonly string _tempRoot;
    private readonly string? _originalScriptOverride;

    public PathPilotInventoryServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "PathPilotInventoryServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _originalScriptOverride = Environment.GetEnvironmentVariable(ScriptEnvVariable);
    }

    [Fact]
    public async Task GetInventoryAsync_ParsesPayloadFromStubScript()
    {
        var service = CreateServiceWithStubScript();

        var snapshot = await service.GetInventoryAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), snapshot.GeneratedAt);
        Assert.Single(snapshot.Runtimes);
        var runtime = snapshot.Runtimes[0];
        Assert.Equal("python", runtime.Id);
        Assert.Equal("Python", runtime.Name);
        Assert.Single(runtime.Installations);
        Assert.Equal("3.11.2", runtime.Installations[0].Version);
        AssertPathEqual(@"C:\Tools", runtime.Installations[0].Directory);
        AssertPathEqual(@"C:\Tools", snapshot.MachinePath.RawValue);
        Assert.Single(snapshot.MachinePath.Entries);
        Assert.Contains("Sample warning", snapshot.Warnings);
    }

    [Fact]
    public async Task SwitchRuntimeAsync_ReturnsSwitchMetadata()
    {
        var service = CreateServiceWithStubScript();
        var request = new PathPilotSwitchRequest("python", "Python", "stub-install", "C:/SDKs/Python/python.exe");

        var result = await service.SwitchRuntimeAsync(request);

        Assert.NotNull(result);
        Assert.Equal("python", result.SwitchResult.RuntimeId);
        Assert.True(result.SwitchResult.Success);
        Assert.True(result.SwitchResult.PathUpdated);
        AssertPathEqual(@"C:\SDKs\Python", result.SwitchResult.TargetDirectory);
        AssertPathEqual(@"C:\ProgramData\TidyWindow\PathPilot\backup-test.reg", result.SwitchResult.BackupPath);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(result.SwitchResult.Timestamp, result.Snapshot.GeneratedAt);
    }

    private PathPilotInventoryService CreateServiceWithStubScript()
    {
        var scriptPath = WriteStubScript();
        Environment.SetEnvironmentVariable(ScriptEnvVariable, scriptPath);
        return new PathPilotInventoryService(new PowerShellInvoker());
    }

    private string WriteStubScript()
    {
        var path = Path.Combine(_tempRoot, "PathPilotStub.ps1");
        var builder = new StringBuilder();
        builder.AppendLine("param(");
        builder.AppendLine("    [string] $ConfigPath,");
        builder.AppendLine("    [string] $Export = 'json',");
        builder.AppendLine("    [string] $SwitchRuntimeId,");
        builder.AppendLine("    [string] $SwitchInstallPath,");
        builder.AppendLine("    [string] $OutputPath");
        builder.AppendLine(")");
        builder.AppendLine();
        builder.AppendLine("function New-StubPayload {");
        builder.AppendLine("    return [pscustomobject]@{");
        builder.AppendLine("        generatedAt = '2025-01-01T00:00:00Z'");
        builder.AppendLine("        runtimes    = @(");
        builder.AppendLine("            [pscustomobject]@{");
        builder.AppendLine("                id = 'python'");
        builder.AppendLine("                name = 'Python'");
        builder.AppendLine("                executableName = 'python.exe'");
        builder.AppendLine("                desiredVersion = '3.11'");
        builder.AppendLine("                description = 'Stub runtime'");
        builder.AppendLine("                installations = @(");
        builder.AppendLine("                    [pscustomobject]@{");
        builder.AppendLine("                        id = 'stub-install'");
        builder.AppendLine("                        directory = 'C:/Tools'");
        builder.AppendLine("                        executablePath = 'C:/Tools/python.exe'");
        builder.AppendLine("                        version = '3.11.2'");
        builder.AppendLine("                        architecture = 'x64'");
        builder.AppendLine("                        source = 'Config'");
        builder.AppendLine("                        isActive = $true");
        builder.AppendLine("                        notes = @('Primary runtime')");
        builder.AppendLine("                    }");
        builder.AppendLine("                )");
        builder.AppendLine("                status = [pscustomobject]@{");
        builder.AppendLine("                    isMissing = $false");
        builder.AppendLine("                    hasDuplicates = $false");
        builder.AppendLine("                    isDrifted = $false");
        builder.AppendLine("                    hasUnknownActive = $false");
        builder.AppendLine("                }");
        builder.AppendLine("                active = [pscustomobject]@{");
        builder.AppendLine("                    executablePath = 'C:/Tools/python.exe'");
        builder.AppendLine("                    pathEntry = 'C:/Tools'");
        builder.AppendLine("                    matchesKnownInstallation = $true");
        builder.AppendLine("                    installationId = 'stub-install'");
        builder.AppendLine("                    source = 'CommandLookup'");
        builder.AppendLine("                }");
        builder.AppendLine("                resolutionOrder = @('C:/Tools/python.exe')");
        builder.AppendLine("            }");
        builder.AppendLine("        )");
        builder.AppendLine("        machinePath = [pscustomobject]@{");
        builder.AppendLine("            raw = 'C:/Tools'");
        builder.AppendLine("            entries = @(");
        builder.AppendLine("                [pscustomobject]@{");
        builder.AppendLine("                    index = 0");
        builder.AppendLine("                    value = 'C:/Tools'");
        builder.AppendLine("                    resolved = 'C:/Tools'");
        builder.AppendLine("                }");
        builder.AppendLine("            )");
        builder.AppendLine("        }");
        builder.AppendLine("        warnings = @('Sample warning')");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("$payload = New-StubPayload");
        builder.AppendLine();
        builder.AppendLine("if ($PSBoundParameters.ContainsKey('SwitchRuntimeId')) {");
        builder.AppendLine("    $switchResult = [pscustomobject]@{");
        builder.AppendLine("        runtimeId = $SwitchRuntimeId");
        builder.AppendLine("        targetDirectory = if ($SwitchInstallPath) { Split-Path -Parent $SwitchInstallPath } else { 'C:/SDKs/Python' }");
        builder.AppendLine("        targetExecutable = if ($SwitchInstallPath) { $SwitchInstallPath } else { 'C:/SDKs/Python/python.exe' }");
        builder.AppendLine("        installationId = 'stub-install'");
        builder.AppendLine("        backupPath = 'C:/ProgramData/TidyWindow/PathPilot/backup-test.reg'");
        builder.AppendLine("        logPath = 'C:/ProgramData/TidyWindow/PathPilot/switch-test.json'");
        builder.AppendLine("        pathUpdated = $true");
        builder.AppendLine("        success = $true");
        builder.AppendLine("        message = 'Switch completed.'");
        builder.AppendLine("        previousPath = 'C:/Tools'");
        builder.AppendLine("        updatedPath = 'C:/SDKs/Python'");
        builder.AppendLine("        timestamp = '2025-01-01T00:00:00Z'");
        builder.AppendLine("    }");
        builder.AppendLine("    Add-Member -InputObject $payload -NotePropertyName 'switchResult' -NotePropertyValue $switchResult -Force | Out-Null");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("$payload | ConvertTo-Json -Depth 6 -Compress");

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
        return path;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ScriptEnvVariable, _originalScriptOverride);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in tests.
        }
    }

    private static void AssertPathEqual(string expected, string? actual)
    {
        Assert.Equal(NormalizePath(expected), NormalizePath(actual), StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').TrimEnd('/');
    }
}
