using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Maintenance;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class RegistryOptimizerViewModelTests
{
    [Fact]
    public async Task ApplyAsync_BlocksWhenRestoreGuardFails()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new RegistryOptimizerTestScope();
            scope.RestoreGuard.EnqueueResult(new SystemRestoreGuardCheckResult(false, null, "Missing checkpoint"));

            scope.ViewModel.Tweaks[0].IsSelected = true;
            await scope.ViewModel.ApplyCommand.ExecuteAsync(null);

            Assert.Equal(0, scope.Service.ApplyCallCount);
            Assert.Equal(1, scope.RestoreGuard.PromptCount);
            Assert.Contains("Blocked", scope.ViewModel.LastOperationSummary);
        });
    }

    [Fact]
    public async Task ApplyAsync_InvokesRegistryServiceWhenGuardSatisfied()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new RegistryOptimizerTestScope();
            scope.RestoreGuard.EnqueueResult(new SystemRestoreGuardCheckResult(true, DateTimeOffset.UtcNow, null));

            scope.ViewModel.Tweaks[0].IsSelected = true;
            await scope.ViewModel.ApplyCommand.ExecuteAsync(null);

            Assert.Equal(1, scope.Service.ApplyCallCount);
            Assert.Equal(0, scope.RestoreGuard.PromptCount);
        });
    }

    [Fact]
    public async Task ApplyAsync_SkipsDisablePagingExecutiveWhenRamTooLow()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new RegistryOptimizerTestScope(getTotalRamGb: () => 8);
            scope.RestoreGuard.EnqueueResult(new SystemRestoreGuardCheckResult(true, DateTimeOffset.UtcNow, null));

            var pagingTweak = scope.ViewModel.Tweaks.Single(t => string.Equals(t.Id, "disable-paging-executive", StringComparison.OrdinalIgnoreCase));
            pagingTweak.IsSelected = true;

            await scope.ViewModel.ApplyCommand.ExecuteAsync(null);

            // The tweak should be skipped (not blocking), so ApplyCallCount is 0 because no other tweaks were selected
            // and the only selected tweak was filtered out, leaving an empty apply list.
            Assert.Equal(0, scope.Service.ApplyCallCount);
            Assert.Contains("Keep kernel in RAM", scope.ViewModel.LastOperationSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Skipped", scope.ViewModel.LastOperationSummary, StringComparison.OrdinalIgnoreCase);
        });
    }

    private sealed class RegistryOptimizerTestScope : IDisposable
    {
        private readonly string _tempDirectory;

        public RegistryOptimizerTestScope(Func<double>? getTotalRamGb = null)
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);

            Preferences = new RegistryPreferenceService(Path.Combine(_tempDirectory, "registry-preferences.json"));
            ActivityLog = new ActivityLogService();
            Service = new TestRegistryOptimizerService();
            RestoreGuard = new TestRestoreGuardService();

            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var navigation = new NavigationService(serviceProvider, ActivityLog, new SmartPageCache());
            Main = new MainViewModel(navigation, ActivityLog);

            ViewModel = new RegistryOptimizerViewModel(ActivityLog, Main, Service, Preferences, RestoreGuard, getTotalRamGb);
        }

        public ActivityLogService ActivityLog { get; }

        public RegistryPreferenceService Preferences { get; }

        public TestRegistryOptimizerService Service { get; }

        public TestRestoreGuardService RestoreGuard { get; }

        public MainViewModel Main { get; }

        public RegistryOptimizerViewModel ViewModel { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class TestRegistryOptimizerService : IRegistryOptimizerService
    {
        private readonly ImmutableArray<RegistryTweakDefinition> _tweaks;
        private readonly RegistryPresetDefinition _preset;

        public TestRegistryOptimizerService()
        {
            var enableOperation = new RegistryOperationDefinition("enable.ps1", null);
            var disableOperation = new RegistryOperationDefinition("disable.ps1", null);
            var pagingEnable = new RegistryOperationDefinition("paging-enable.ps1", null);
            var pagingDisable = new RegistryOperationDefinition("paging-disable.ps1", null);

            var sample = new RegistryTweakDefinition(
                "sample.tweak",
                "Sample tweak",
                "Performance",
                "Sample description",
                "Medium",
                "perf",
                false,
                null,
                null,
                null,
                enableOperation,
                disableOperation);

            var disablePagingExecutive = new RegistryTweakDefinition(
                "disable-paging-executive",
                "Keep kernel in RAM",
                "Performance",
                "Keeps kernel memory resident",
                "Medium",
                "ram",
                false,
                null,
                null,
                null,
                pagingEnable,
                pagingDisable);

            _tweaks = ImmutableArray.Create(sample, disablePagingExecutive);
            _preset = new RegistryPresetDefinition(
                "default",
                "Default",
                "Default preset",
                "perf",
                true,
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sample.tweak"] = false,
                    ["disable-paging-executive"] = false
                });
        }

        public int ApplyCallCount { get; private set; }

        public IReadOnlyList<RegistryTweakDefinition> Tweaks => _tweaks;

        public IReadOnlyList<RegistryPresetDefinition> Presets => new[] { _preset };

        public RegistryTweakDefinition GetTweak(string tweakId) => _tweaks.First(tweak => string.Equals(tweak.Id, tweakId, StringComparison.OrdinalIgnoreCase));

        public RegistryOperationPlan BuildPlan(IEnumerable<RegistrySelection> selections)
        {
            var invocations = selections
                .Select(selection => new RegistryScriptInvocation(
                    selection.TweakId,
                    "Apply tweak",
                    selection.TargetState,
                    $"{selection.TweakId}.ps1",
                    ImmutableDictionary<string, object?>.Empty))
                .ToImmutableArray();

            return new RegistryOperationPlan(invocations, ImmutableArray<RegistryScriptInvocation>.Empty);
        }

        public Task<RegistryOperationResult> ApplyAsync(RegistryOperationPlan plan, CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            var summaries = plan.ApplyOperations
                .Select(operation => new RegistryExecutionSummary(
                    operation,
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty,
                    0))
                .ToImmutableArray();

            return Task.FromResult(new RegistryOperationResult(summaries));
        }

        public Task<RegistryRestorePoint?> SaveRestorePointAsync(IEnumerable<RegistrySelection> selections, RegistryOperationPlan plan, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<RegistryRestorePoint?>(null);
        }

        public RegistryRestorePoint? TryGetLatestRestorePoint() => null;

        public Task<RegistryOperationResult> ApplyRestorePointAsync(RegistryRestorePoint restorePoint, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public void DeleteRestorePoint(RegistryRestorePoint restorePoint)
        {
        }
    }

    private sealed class TestRestoreGuardService : ISystemRestoreGuardService
    {
        private readonly Queue<SystemRestoreGuardCheckResult> _results = new();
        private SystemRestoreGuardPrompt? _pending;

        public int PromptCount { get; private set; }

        public event EventHandler<SystemRestoreGuardPromptEventArgs>? PromptRequested;

        public void EnqueueResult(SystemRestoreGuardCheckResult result) => _results.Enqueue(result);

        public Task<SystemRestoreGuardCheckResult> CheckAsync(TimeSpan freshnessThreshold, CancellationToken cancellationToken = default)
        {
            if (_results.Count == 0)
            {
                return Task.FromResult(new SystemRestoreGuardCheckResult(true, DateTimeOffset.UtcNow, null));
            }

            return Task.FromResult(_results.Dequeue());
        }

        public void RequestPrompt(SystemRestoreGuardPrompt prompt)
        {
            PromptCount++;
            _pending = prompt;
            PromptRequested?.Invoke(this, new SystemRestoreGuardPromptEventArgs(prompt));
        }

        public bool TryConsumePendingPrompt(out SystemRestoreGuardPrompt prompt)
        {
            prompt = _pending!;
            var hadPrompt = _pending is not null;
            _pending = null;
            return hadPrompt;
        }
    }
}
