using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Install;
using TidyWindow.Core.Maintenance;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class MaintenanceAutoUpdateSchedulerTests
{
    [Fact]
    public async Task RunOnceAsync_WhenAutomationDisabled_SkipsAndLogsReason()
    {
        using var harness = new MaintenanceAutomationTestHarness();
        harness.SetInventory(new TestInventoryPackage("winget", "Contoso.Tool", "Contoso Tool", "1.0.0", "1.1.0"));

        var result = await harness.Scheduler.RunOnceAsync();

        Assert.True(result.WasSkipped);
        Assert.Equal("Automation disabled", result.SkipReason);

        var entry = Assert.Single(harness.ActivityLog.GetSnapshot());
        Assert.Equal(ActivityLogLevel.Information, entry.Level);
        Assert.Contains("Automation run skipped", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Automation disabled", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunOnceAsync_WhenNoPackagesRequireUpdates_UpdatesLastRun()
    {
        using var harness = new MaintenanceAutomationTestHarness();
        harness.SetInventory(new TestInventoryPackage("winget", "Contoso.Tool", "Contoso Tool", "1.0.0", null));
        harness.SetUpdateResults();
        await harness.EnableUpdateAllAutomationAsync();

        var result = await harness.Scheduler.RunOnceAsync();

        Assert.False(result.WasSkipped);
        Assert.Empty(result.Actions);

        var persisted = harness.Store.Get();
        Assert.NotNull(persisted.LastRunUtc);

        var entry = Assert.Single(harness.ActivityLog.GetSnapshot());
        Assert.Equal(ActivityLogLevel.Information, entry.Level);
        Assert.Contains("no packages", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunOnceAsync_WhenUpdatesExist_RunsMaintenanceScripts()
    {
        using var harness = new MaintenanceAutomationTestHarness();
        harness.SetInventory(new TestInventoryPackage("winget", "Contoso.Tool", "Contoso Tool", "1.0.0", "1.2.0"));
        harness.SetUpdateResults(new TestUpdateResult("Contoso.Tool", true, true, "Update applied", "1.0.0", "1.2.0"));
        await harness.EnableUpdateAllAutomationAsync();

        var result = await harness.Scheduler.RunOnceAsync();

        var action = Assert.Single(result.Actions);
        Assert.True(action.Success);
        Assert.True(action.Attempted);
        Assert.Equal("Contoso Tool", action.DisplayName);

        var logEntry = Assert.Single(harness.ActivityLog.GetSnapshot());
        Assert.Equal(ActivityLogLevel.Success, logEntry.Level);
        Assert.Contains("updated 1 package", logEntry.Message, StringComparison.OrdinalIgnoreCase);

        var updateLog = harness.ReadUpdateLog();
        var invocation = Assert.Single(updateLog);
        Assert.Contains("Contoso.Tool", invocation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunOnceAsync_WhenScriptReportsFailure_LogsWarningAndReturnsFailedAction()
    {
        using var harness = new MaintenanceAutomationTestHarness();
        harness.SetInventory(new TestInventoryPackage("winget", "Contoso.Tool", "Contoso Tool", "1.0.0", "1.2.0"));
        harness.SetUpdateResults(new TestUpdateResult("Contoso.Tool", false, true, "Simulated failure", "1.0.0", "1.2.0"));
        await harness.EnableUpdateAllAutomationAsync();

        var result = await harness.Scheduler.RunOnceAsync();

        var action = Assert.Single(result.Actions);
        Assert.False(action.Success);
        Assert.True(action.Attempted);
        Assert.Contains("Simulated failure", action.Message, StringComparison.OrdinalIgnoreCase);

        var logEntry = Assert.Single(harness.ActivityLog.GetSnapshot());
        Assert.Equal(ActivityLogLevel.Warning, logEntry.Level);
        Assert.Contains("failing package", logEntry.Message, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record TestInventoryPackage(
    string Manager,
    string PackageId,
    string Name,
    string InstalledVersion,
    string? AvailableVersion,
    string Source = "winget");

internal sealed record TestUpdateResult(
    string PackageId,
    bool Succeeded,
    bool Attempted,
    string Summary,
    string InstalledVersion,
    string LatestVersion,
    bool ThrowError = false);

internal sealed class MaintenanceAutomationTestHarness : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _root;
    private readonly string _inventoryScriptPath;
    private readonly string _inventoryPayloadPath;
    private readonly string _updateScriptPath;
    private readonly string _updateConfigPath;
    private readonly string _updateLogPath;
    private readonly string _settingsRoot;
    private readonly string? _originalInventoryOverride;
    private readonly string? _originalUpdateOverride;
    private bool _disposed;

    public MaintenanceAutomationTestHarness()
    {
        _root = Path.Combine(Path.GetTempPath(), "TidyWindowTests", "MaintenanceAutomation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _settingsRoot = Path.Combine(_root, "settings");
        Directory.CreateDirectory(_settingsRoot);

        _inventoryScriptPath = Path.Combine(_root, "inventory.ps1");
        _inventoryPayloadPath = Path.Combine(_root, "inventory.json");
        File.WriteAllText(_inventoryScriptPath, BuildInventoryScript());
        File.WriteAllText(_inventoryPayloadPath, "{}");
        _originalInventoryOverride = Environment.GetEnvironmentVariable("TIDYWINDOW_PACKAGE_INVENTORY_SCRIPT");
        Environment.SetEnvironmentVariable("TIDYWINDOW_PACKAGE_INVENTORY_SCRIPT", _inventoryScriptPath);

        _updateScriptPath = Path.Combine(_root, "update.ps1");
        _updateConfigPath = Path.Combine(_root, "update-config.json");
        _updateLogPath = Path.Combine(_root, "update-log.txt");
        File.WriteAllText(_updateScriptPath, BuildUpdateScript());
        File.WriteAllText(_updateConfigPath, "[]");
        _originalUpdateOverride = Environment.GetEnvironmentVariable("TIDYWINDOW_PACKAGE_UPDATE_SCRIPT");
        Environment.SetEnvironmentVariable("TIDYWINDOW_PACKAGE_UPDATE_SCRIPT", _updateScriptPath);

        var invoker = new PowerShellInvoker();
        var catalog = new InstallCatalogService();
        InventoryService = new PackageInventoryService(invoker, catalog);
        MaintenanceService = new PackageMaintenanceService(invoker);
        Store = new MaintenanceAutomationSettingsStore(_settingsRoot);
        Preferences = new UserPreferencesService();
        ActivityLog = new ActivityLogService();
        WorkTracker = new AutomationWorkTracker();
        Scheduler = new MaintenanceAutoUpdateScheduler(Store, InventoryService, MaintenanceService, Preferences, ActivityLog, WorkTracker);
    }

    public MaintenanceAutoUpdateScheduler Scheduler { get; }

    public MaintenanceAutomationSettingsStore Store { get; }

    public ActivityLogService ActivityLog { get; }

    public UserPreferencesService Preferences { get; }

    public AutomationWorkTracker WorkTracker { get; }

    public PackageInventoryService InventoryService { get; }

    public PackageMaintenanceService MaintenanceService { get; }

    public void SetInventory(params TestInventoryPackage[] packages)
    {
        var payload = new
        {
            generatedAt = DateTimeOffset.UtcNow.ToString("O"),
            warnings = Array.Empty<string>(),
            packages = packages.Select(package => new
            {
                manager = package.Manager,
                id = package.PackageId,
                name = package.Name,
                installedVersion = package.InstalledVersion,
                availableVersion = package.AvailableVersion,
                source = package.Source
            })
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        File.WriteAllText(_inventoryPayloadPath, json);
    }

    public void SetUpdateResults(params TestUpdateResult[] results)
    {
        var payload = results.Select(result => new
        {
            packageId = result.PackageId,
            succeeded = result.Succeeded,
            attempted = result.Attempted,
            summary = result.Summary,
            installedVersion = result.InstalledVersion,
            latestVersion = result.LatestVersion,
            throwError = result.ThrowError
        });

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        File.WriteAllText(_updateConfigPath, json);
    }

    public Task EnableUpdateAllAutomationAsync()
    {
        var settings = new MaintenanceAutomationSettings(
            automationEnabled: true,
            updateAllPackages: true,
            intervalMinutes: MaintenanceAutomationSettings.MinimumIntervalMinutes,
            lastRunUtc: null,
            targets: Array.Empty<MaintenanceAutomationTarget>());

        return Scheduler.ApplySettingsAsync(settings, runImmediately: false);
    }

    public string[] ReadUpdateLog()
    {
        if (!File.Exists(_updateLogPath))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(_updateLogPath);
    }

    private static string BuildInventoryScript()
    {
        return """
param()
Set-StrictMode -Version Latest
$payloadPath = Join-Path $PSScriptRoot 'inventory.json'
if (-not (Test-Path $payloadPath)) {
    throw 'inventory.json missing'
}
Get-Content -Path $payloadPath -Raw
""";
    }

    private static string BuildUpdateScript()
    {
        return """
param(
    [Parameter(Mandatory=$true)][string]$Manager,
    [Parameter(Mandatory=$true)][string]$PackageId,
    [Parameter(Mandatory=$true)][string]$DisplayName,
    [string]$TargetVersion
)
Set-StrictMode -Version Latest
$configPath = Join-Path $PSScriptRoot 'update-config.json'
$logPath = Join-Path $PSScriptRoot 'update-log.txt'
Add-Content -Path $logPath -Value "$PackageId|$TargetVersion"
if (-not (Test-Path $configPath)) {
    throw 'update-config.json missing'
}
$configJson = Get-Content -Path $configPath -Raw
if ([string]::IsNullOrWhiteSpace($configJson)) {
    throw 'update configuration empty'
}
$config = $configJson | ConvertFrom-Json
if ($null -eq $config) {
    throw 'update configuration invalid'
}
$match = $null
if ($config -is [System.Array]) {
    $match = $config | Where-Object { $_.packageId -eq $PackageId } | Select-Object -First 1
} elseif ($config.packageId -eq $PackageId) {
    $match = $config
}
if ($null -eq $match) {
    throw "Package '$PackageId' is not configured"
}
if ($match.throwError -eq $true) {
    throw "Configured failure for $PackageId"
}
$result = [pscustomobject]@{
    operation = 'update'
    manager = $Manager
    packageId = $PackageId
    displayName = $DisplayName
    statusBefore = 'Installed'
    statusAfter = 'Updated'
    installedVersion = $match.installedVersion
    latestVersion = $match.latestVersion
    succeeded = [bool]$match.succeeded
    attempted = [bool]$match.attempted
    exitCode = 0
    summary = $match.summary
    requestedVersion = $TargetVersion
    output = @('completed')
    errors = @()
}
$result | ConvertTo-Json -Depth 5
""";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Scheduler.Dispose();

        Environment.SetEnvironmentVariable("TIDYWINDOW_PACKAGE_INVENTORY_SCRIPT", _originalInventoryOverride);
        Environment.SetEnvironmentVariable("TIDYWINDOW_PACKAGE_UPDATE_SCRIPT", _originalUpdateOverride);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
