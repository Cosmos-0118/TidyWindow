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
        return new PerformanceLabViewModel(fake, activity);
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
        Assert.Contains("exitCode: 0", vm.HardwareStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyKernelPreset_ReportsApplied()
    {
        var fake = new FakePerformanceLabService();
        var vm = CreateVm(fake);

        await vm.ApplyKernelPresetCommand.ExecuteAsync(null);

        Assert.True(vm.IsKernelSuccess);
        Assert.Contains("exitCode: 0", vm.KernelStatusMessage, StringComparison.OrdinalIgnoreCase);
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

        public Task<PowerShellInvocationResult> DetectHardwareReservedMemoryAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SuccessResult("mode: Detect"));
        }

        public Task<PowerPlanStatus> GetPowerPlanStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlanStatus);
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
