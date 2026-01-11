using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Performance;
using Xunit;

namespace TidyWindow.App.Tests;

public class PerformanceLabViewModelTests
{
    private static PerformanceLabViewModel CreateVm(FakePerformanceLabService fake)
    {
        var activity = new ActivityLogService();
        var automationRunner = new PerformanceLabAutomationRunner(new PerformanceLabAutomationSettingsStore(), fake, activity, new AutomationWorkTracker());
        return new PerformanceLabViewModel(fake, activity, automationRunner);
    }

    [Fact]
    public async Task EnableUltimatePlan_SetsSuccessAndUltimate()
    {
        var fake = new FakePerformanceLabService();
        var vm = CreateVm(fake);

        await vm.EnableUltimatePlanCommand.ExecuteAsync(null);

        Assert.True(vm.IsPowerPlanSuccess);
        Assert.Equal("Ultimate Performance enabled", vm.PowerPlanStatusMessage);
    }

    [Fact]
    public async Task ApplyServiceTemplate_CreatesBackupStatus()
    {
        var fake = new FakePerformanceLabService();
        var vm = CreateVm(fake);

        await vm.ApplyServiceTemplateCommand.ExecuteAsync(fake.TemplateOption);

        Assert.True(vm.IsServiceSuccess);
        Assert.Contains("Applied service template", vm.ServiceStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectHardwareReserved_ReportsDetection()
    {
        var fake = new FakePerformanceLabService();
        var vm = CreateVm(fake);

        await vm.DetectHardwareReservedCommand.ExecuteAsync(null);

        Assert.True(vm.IsHardwareSuccess);
        Assert.Contains("mode: Detect", vm.HardwareStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyKernelPreset_ReportsApplied()
    {
        var fake = new FakePerformanceLabService();
        var vm = CreateVm(fake);

        await vm.ApplyKernelPresetCommand.ExecuteAsync(null);

        Assert.True(vm.IsKernelSuccess);
        Assert.Contains("action: Recommended", vm.KernelStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisableVbsHvci_ReportsStatus()
    {
        var fake = new FakePerformanceLabService();
        var vm = CreateVm(fake);

        await vm.DisableVbsHvciCommand.ExecuteAsync(null);

        Assert.True(vm.IsVbsSuccess);
        Assert.Contains("DisableVbsHvci", vm.VbsStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanupEtwMinimal_ReportsStatus()
    {
        var fake = new FakePerformanceLabService();
        var vm = CreateVm(fake);

        await vm.CleanupEtwMinimalCommand.ExecuteAsync(null);

        Assert.True(vm.IsEtwSuccess);
        Assert.Contains("CleanupEtw", vm.EtwStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakePerformanceLabService : IPerformanceLabService
    {
        public PowerPlanStatus PlanStatus { get; private set; } = new("default", "Balanced", false, "state.json");
        public ServiceSlimmingStatus ServiceStatus { get; private set; } = new(null);
        public PerformanceTemplateOption TemplateOption { get; } = new() { Id = "Balanced", Name = "Balanced", Description = "Test", ServiceCount = 1 };

        public Task<PowerShellInvocationResult> ApplyHardwareReservedFixAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: ApplyFix"));
        }

        public Task<PowerShellInvocationResult> ApplyKernelBootActionAsync(string action, bool skipRestorePoint = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult($"action: {action}"));
        }

        public Task<PowerShellInvocationResult> ApplyServiceSlimmingAsync(string? templateId = null, CancellationToken cancellationToken = default)
        {
            ServiceStatus = new ServiceSlimmingStatus("service-backup.json");
            return Task.FromResult(SuccessResult("mode: Applied"));
        }

        public Task<string?> DetectServiceTemplateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(TemplateOption.Id);
        }

        public Task<PowerShellInvocationResult> DetectVbsHvciAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: DetectVbsHvci"));
        }

        public Task<PowerShellInvocationResult> DisableVbsHvciAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: DisableVbsHvci"));
        }

        public Task<PowerShellInvocationResult> RestoreVbsHvciAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: RestoreVbsHvci"));
        }

        public Task<PowerShellInvocationResult> DetectEtwTracingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: DetectEtw"));
        }

        public Task<PowerShellInvocationResult> CleanupEtwTracingAsync(string mode = "Minimal", CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult($"mode: CleanupEtw ({mode})"));
        }

        public Task<PowerShellInvocationResult> RestoreEtwTracingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: RestoreEtw"));
        }

        public Task<PowerShellInvocationResult> DetectDirectStorageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: DetectDirectStorage"));
        }

        public Task<PowerShellInvocationResult> ApplyIoPriorityBoostAsync(bool boostIoPriority = true, bool boostThreadPriority = true, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult($"mode: ApplyIoBoost io:{boostIoPriority} threads:{boostThreadPriority}"));
        }

        public Task<PowerShellInvocationResult> RestoreIoPriorityAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: RestoreIoBoost"));
        }

        public Task<PowerShellInvocationResult> DetectAutoTuneAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("state: detected"));
        }

        public Task<PowerShellInvocationResult> StartAutoTuneAsync(string? processNames = null, string? preset = null, CancellationToken cancellationToken = default)
        {
            var presetPart = string.IsNullOrWhiteSpace(preset) ? "LatencyBoost" : preset;
            var procPart = string.IsNullOrWhiteSpace(processNames) ? "none" : processNames;
            return Task.FromResult(SuccessResult($"start: {presetPart} for {procPart}"));
        }

        public Task<PowerShellInvocationResult> StopAutoTuneAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: StopAutoTune"));
        }

        public Task<PowerShellInvocationResult> DetectPagefileAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: DetectPagefile"));
        }

        public Task<PowerShellInvocationResult> ApplyPagefilePresetAsync(string preset, string? targetDrive = null, int? initialMb = null, int? maxMb = null, bool sweepWorkingSets = false, bool includePinned = false, CancellationToken cancellationToken = default)
        {
            var presetPart = string.IsNullOrWhiteSpace(preset) ? "SystemManaged" : preset;
            var details = $"preset: {presetPart}, drive: {targetDrive ?? "default"}, initial: {initialMb?.ToString() ?? "auto"}, max: {maxMb?.ToString() ?? "auto"}, sweep: {sweepWorkingSets}, pinned: {includePinned}";
            return Task.FromResult(SuccessResult(details));
        }

        public Task<PowerShellInvocationResult> SweepWorkingSetsAsync(bool includePinned = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult($"mode: SweepWorkingSets (pinned: {includePinned})"));
        }

        public Task<PowerShellInvocationResult> DetectSchedulerAffinityAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: DetectScheduler"));
        }

        public Task<PowerShellInvocationResult> ApplySchedulerAffinityAsync(string preset, string? processNames = null, CancellationToken cancellationToken = default)
        {
            var presetPart = string.IsNullOrWhiteSpace(preset) ? "Balanced" : preset;
            var procPart = string.IsNullOrWhiteSpace(processNames) ? "defaults" : processNames;
            return Task.FromResult(SuccessResult($"mode: ApplyScheduler ({presetPart}) for {procPart}"));
        }

        public Task<PowerShellInvocationResult> RestoreSchedulerAffinityAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: RestoreScheduler"));
        }

        public Task<PowerShellInvocationResult> DetectHardwareReservedMemoryAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: Detect"));
        }

        public Task<PowerPlanStatus> GetPowerPlanStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlanStatus);
        }

        public Task<KernelBootStatus> GetKernelBootStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new KernelBootStatus(true, "kernel defaults mocked", new List<string> { "mock output" }, null));
        }

        public ServiceSlimmingStatus GetServiceSlimmingStatus()
        {
            return ServiceStatus;
        }

        public Task<PowerShellInvocationResult> RestoreMemoryCompressionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: RestoreCompression"));
        }

        public Task<PowerShellInvocationResult> RestorePowerPlanAsync(CancellationToken cancellationToken = default)
        {
            PlanStatus = new PowerPlanStatus("default", "Balanced", false, PlanStatus.LastBackupPath);
            return Task.FromResult(SuccessResult("mode: Restore"));
        }

        public Task<PowerShellInvocationResult> RestoreServicesAsync(string? statePath = null, CancellationToken cancellationToken = default)
        {
            ServiceStatus = new ServiceSlimmingStatus(statePath);
            return Task.FromResult(SuccessResult("mode: Restore"));
        }

        public Task<PowerShellInvocationResult> EnableUltimatePowerPlanAsync(CancellationToken cancellationToken = default)
        {
            PlanStatus = new PowerPlanStatus("ultimate", "Ultimate Performance", true, "state.json");
            return Task.FromResult(SuccessResult("mode: Enabled"));
        }

        private static PowerShellInvocationResult SuccessResult(string line)
        {
            return new PowerShellInvocationResult(new List<string> { line }, Array.Empty<string>(), 0);
        }
    }
}
