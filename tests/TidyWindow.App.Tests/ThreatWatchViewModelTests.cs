using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Processes;
using TidyWindow.Core.Processes.ThreatWatch;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class ThreatWatchViewModelTests
{
    [Fact]
    public async Task QuarantineAsync_PersistsDefenderVerdict()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var tempStatePath = Path.Combine(tempRoot, "process-state.json");
        var previousStateOverride = Environment.GetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH");
        Environment.SetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH", tempStatePath);

        try
        {
            var stateStore = new ProcessStateStore();
            var provider = new TestThreatIntelProvider();
            var detectionService = new ThreatWatchDetectionService(stateStore, new[] { provider });
            var scanService = new ThreatWatchScanService(detectionService);
            var confirmationService = new AlwaysConfirmService();

            var services = new ServiceCollection().BuildServiceProvider();
            var activityLog = new ActivityLogService();
            var navigationService = new NavigationService(services, activityLog, new SmartPageCache());
            var mainViewModel = new MainViewModel(navigationService, activityLog);
            var viewModel = new ThreatWatchViewModel(scanService, stateStore, confirmationService, mainViewModel);

            var tempFile = Path.Combine(tempRoot, "suspicious.exe");
            await File.WriteAllTextAsync(tempFile, "malware payload");
            var expectedSha = ComputeSha256(tempFile);

            var hit = new SuspiciousProcessHit(
                id: "hit-1",
                processName: "suspicious.exe",
                filePath: tempFile,
                level: SuspicionLevel.Red,
                matchedRules: new[] { "rule" },
                observedAtUtc: DateTimeOffset.UtcNow,
                hash: null,
                source: "tests",
                notes: null);

            var hitViewModel = new ThreatWatchHitViewModel(viewModel, hit);
            await viewModel.QuarantineAsync(hitViewModel);

            var entry = stateStore.GetQuarantineEntries().Single();
            Assert.Equal(ThreatIntelVerdict.KnownBad, entry.Verdict);
            Assert.Equal(TestThreatIntelProvider.Source, entry.VerdictSource);
            Assert.Equal(TestThreatIntelProvider.Details, entry.VerdictDetails);
            Assert.Equal(expectedSha, entry.Sha256);
            Assert.Contains("Defender flagged the file", hitViewModel.LastActionMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH", previousStateOverride);
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }

    [Fact]
    public async Task ScanFileAsync_LogsVerdictAndDetails()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var tempStatePath = Path.Combine(tempRoot, "process-state.json");
        var previousStateOverride = Environment.GetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH");
        Environment.SetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH", tempStatePath);

        try
        {
            var stateStore = new ProcessStateStore();
            var provider = new TestThreatIntelProvider();
            var detectionService = new ThreatWatchDetectionService(stateStore, new[] { provider });
            var scanService = new ThreatWatchScanService(detectionService);
            var confirmationService = new AlwaysConfirmService();

            var activityLog = new ActivityLogService();
            var services = new ServiceCollection().BuildServiceProvider();
            var navigationService = new NavigationService(services, activityLog, new SmartPageCache());
            var mainViewModel = new MainViewModel(navigationService, activityLog);
            var viewModel = new ThreatWatchViewModel(scanService, stateStore, confirmationService, mainViewModel);

            var tempFile = Path.Combine(tempRoot, "benign.exe");
            await File.WriteAllTextAsync(tempFile, "benign payload");

            var hit = new SuspiciousProcessHit(
                id: "hit-2",
                processName: "benign.exe",
                filePath: tempFile,
                level: SuspicionLevel.Yellow,
                matchedRules: new[] { "rule" },
                observedAtUtc: DateTimeOffset.UtcNow,
                hash: null,
                source: "tests",
                notes: null);

            var hitViewModel = new ThreatWatchHitViewModel(viewModel, hit);
            await viewModel.ScanFileAsync(hitViewModel);

            Assert.Contains("Defender flagged the file", hitViewModel.LastActionMessage);

            var entry = activityLog.GetSnapshot().First();
            Assert.Equal("Threat Watch", entry.Source);
            Assert.Equal("Scanned benign.exe", entry.Message);
            Assert.Contains(tempFile, entry.Details);
            Assert.Contains(hitViewModel.LastActionMessage, entry.Details);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH", previousStateOverride);
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }

    [Fact]
    public async Task ScanFileAsync_IgnoresConcurrentClicks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var tempStatePath = Path.Combine(tempRoot, "process-state.json");
        var previousStateOverride = Environment.GetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH");
        Environment.SetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH", tempStatePath);

        try
        {
            var stateStore = new ProcessStateStore();
            var provider = new BlockingThreatIntelProvider();
            var detectionService = new ThreatWatchDetectionService(stateStore, new[] { provider });
            var scanService = new ThreatWatchScanService(detectionService);
            var confirmationService = new AlwaysConfirmService();

            var activityLog = new ActivityLogService();
            var services = new ServiceCollection().BuildServiceProvider();
            var navigationService = new NavigationService(services, activityLog, new SmartPageCache());
            var mainViewModel = new MainViewModel(navigationService, activityLog);
            var viewModel = new ThreatWatchViewModel(scanService, stateStore, confirmationService, mainViewModel);

            var tempFile = Path.Combine(tempRoot, "blocking.exe");
            await File.WriteAllTextAsync(tempFile, "payload");
            var expectedSha = ComputeSha256(tempFile);

            var hit = new SuspiciousProcessHit(
                id: "hit-3",
                processName: "blocking.exe",
                filePath: tempFile,
                level: SuspicionLevel.Orange,
                matchedRules: new[] { "rule" },
                observedAtUtc: DateTimeOffset.UtcNow,
                hash: null,
                source: "tests",
                notes: null);

            var hitViewModel = new ThreatWatchHitViewModel(viewModel, hit);

            var firstScan = viewModel.ScanFileAsync(hitViewModel);
            Assert.True(hitViewModel.IsBusy);
            Assert.Equal("Running Defender file scan...", hitViewModel.LastActionMessage);

            var secondScan = viewModel.ScanFileAsync(hitViewModel);
            await secondScan; // should exit immediately without altering state

            // Release the blocker and complete the first scan
            provider.Complete(ThreatIntelResult.KnownBad(expectedSha, BlockingThreatIntelProvider.Source, BlockingThreatIntelProvider.Details));
            await firstScan;

            Assert.False(hitViewModel.IsBusy);
            Assert.Contains(BlockingThreatIntelProvider.Source, hitViewModel.LastActionMessage);

            var entries = activityLog.GetSnapshot();
            Assert.Single(entries); // only one scan was logged
        }
        finally
        {
            Environment.SetEnvironmentVariable("TIDYWINDOW_PROCESS_STATE_PATH", previousStateOverride);
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private sealed class AlwaysConfirmService : IUserConfirmationService
    {
        public bool Confirm(string title, string message) => true;
    }

    private sealed class TestThreatIntelProvider : IThreatIntelProvider
    {
        public const string Source = "TestDefender";
        public const string Details = "Simulated detection";

        public ThreatIntelProviderKind Kind => ThreatIntelProviderKind.Local;

        public ValueTask<ThreatIntelResult> EvaluateAsync(string filePath, string? sha256, CancellationToken cancellationToken)
        {
            var hash = sha256 ?? ComputeSha256(filePath);
            return ValueTask.FromResult(ThreatIntelResult.KnownBad(hash, Source, Details));
        }
    }

    private sealed class BlockingThreatIntelProvider : IThreatIntelProvider
    {
        public const string Source = "BlockingProvider";
        public const string Details = "Delayed verdict";

        private readonly TaskCompletionSource<ThreatIntelResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ThreatIntelProviderKind Kind => ThreatIntelProviderKind.Local;

        public ValueTask<ThreatIntelResult> EvaluateAsync(string filePath, string? sha256, CancellationToken cancellationToken)
        {
            return new ValueTask<ThreatIntelResult>(_tcs.Task);
        }

        public void Complete(ThreatIntelResult result)
        {
            _tcs.TrySetResult(result);
        }
    }
}
