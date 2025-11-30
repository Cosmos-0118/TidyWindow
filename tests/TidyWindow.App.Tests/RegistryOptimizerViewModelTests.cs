using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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

    private sealed class RegistryOptimizerTestScope : IDisposable
    {
        private readonly string _tempDirectory;

        public RegistryOptimizerTestScope()
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

            ViewModel = new RegistryOptimizerViewModel(ActivityLog, Main, Service, Preferences, RestoreGuard);
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
        private readonly RegistryTweakDefinition _tweak;
        private readonly RegistryPresetDefinition _preset;

        public TestRegistryOptimizerService()
        {
            var enableOperation = new RegistryOperationDefinition("enable.ps1", null);
            var disableOperation = new RegistryOperationDefinition("disable.ps1", null);
            _tweak = new RegistryTweakDefinition(
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
            _preset = new RegistryPresetDefinition(
                "default",
                "Default",
                "Default preset",
                "perf",
                true,
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sample.tweak"] = false
                });
        }

        public int ApplyCallCount { get; private set; }

        public IReadOnlyList<RegistryTweakDefinition> Tweaks => new[] { _tweak };

        public IReadOnlyList<RegistryPresetDefinition> Presets => new[] { _preset };

        public RegistryTweakDefinition GetTweak(string tweakId) => _tweak;

        public RegistryOperationPlan BuildPlan(IEnumerable<RegistrySelection> selections)
        {
            var invocation = new RegistryScriptInvocation(
                _tweak.Id,
                "Apply tweak",
                true,
                "script.ps1",
                ImmutableDictionary<string, object?>.Empty);

            return new RegistryOperationPlan(ImmutableArray.Create(invocation), ImmutableArray<RegistryScriptInvocation>.Empty);
        }

        public Task<RegistryOperationResult> ApplyAsync(RegistryOperationPlan plan, CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            var summary = new RegistryExecutionSummary(
                plan.ApplyOperations[0],
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                0);
            return Task.FromResult(new RegistryOperationResult(ImmutableArray.Create(summary)));
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
