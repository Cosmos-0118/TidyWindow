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
using TidyWindow.Core.Processes.AntiSystem;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class AntiSystemViewModelTests
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
            var detectionService = new AntiSystemDetectionService(stateStore, new[] { provider });
            var scanService = new AntiSystemScanService(detectionService);
            var confirmationService = new AlwaysConfirmService();

            var services = new ServiceCollection().BuildServiceProvider();
            var activityLog = new ActivityLogService();
            var navigationService = new NavigationService(services, activityLog, new SmartPageCache());
            var mainViewModel = new MainViewModel(navigationService, activityLog);
            var viewModel = new AntiSystemViewModel(scanService, stateStore, confirmationService, mainViewModel);

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

            var hitViewModel = new AntiSystemHitViewModel(viewModel, hit);
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
}
