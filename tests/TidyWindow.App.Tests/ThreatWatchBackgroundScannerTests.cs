using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using TidyWindow.Core.Processes;
using TidyWindow.Core.Processes.ThreatWatch;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class ThreatWatchBackgroundScannerTests
{
    [Fact]
    public async Task LogsSuccessWhenScanIsClear()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new BackgroundScannerTestScope();
            var scanInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var scanner = scope.CreateScanner(_ =>
            {
                scanInvoked.TrySetResult(true);
                return Task.FromResult(scope.CreateResult(Array.Empty<SuspiciousProcessHit>()));
            });

            await scanInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var entry = await scope.WaitForThreatWatchEntryAsync(TimeSpan.FromSeconds(1));
            Assert.NotNull(entry);
            Assert.Equal(ActivityLogLevel.Success, entry.Level);
            Assert.Equal("Threat Watch", entry.Source);
            Assert.Equal("Background scan is clear.", entry.Message);
        });
    }

    [Fact]
    public async Task LogsErrorWhenCriticalHitDetected()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new BackgroundScannerTestScope();
            var scanInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hit = scope.CreateHit("malware.exe", SuspicionLevel.Red);

            using var scanner = scope.CreateScanner(_ =>
            {
                scanInvoked.TrySetResult(true);
                return Task.FromResult(scope.CreateResult(new[] { hit }));
            });

            await scanInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var entry = await scope.WaitForThreatWatchEntryAsync(TimeSpan.FromSeconds(1));
            Assert.NotNull(entry);
            Assert.Equal(ActivityLogLevel.Error, entry.Level);
            Assert.Equal("Threat Watch", entry.Source);
            Assert.Contains("flagged 1 suspicious process", entry.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task StopsScanningWhenPulseGuardIsDisabled()
    {
        await WpfTestHelper.RunAsync(async () =>
        {
            using var scope = new BackgroundScannerTestScope();
            var firstScan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondScan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scanCount = 0;

            using var scanner = scope.CreateScanner(_ =>
            {
                var count = Interlocked.Increment(ref scanCount);
                if (count == 1)
                {
                    firstScan.TrySetResult(true);
                }
                else if (count == 2)
                {
                    secondScan.TrySetResult(true);
                }

                return Task.FromResult(scope.CreateResult(Array.Empty<SuspiciousProcessHit>()));
            }, intervalOverride: TimeSpan.FromMilliseconds(500));

            await firstScan.Task.WaitAsync(TimeSpan.FromSeconds(1));

            scope.Preferences.SetPulseGuardEnabled(false);
            await Task.Delay(100);

            var completed = await Task.WhenAny(secondScan.Task, Task.Delay(300));
            Assert.NotSame(secondScan.Task, completed);
            Assert.False(secondScan.Task.IsCompleted);
        });
    }

    private sealed class BackgroundScannerTestScope : IDisposable
    {
        private readonly string? _previousLocalAppData;
        private readonly string _tempLocalAppData;

        public BackgroundScannerTestScope()
        {
            _previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            _tempLocalAppData = Path.Combine(Path.GetTempPath(), "TidyWindowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempLocalAppData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", _tempLocalAppData);

            ActivityLog = new ActivityLogService();
            Preferences = new UserPreferencesService();
            Preferences.SetRunInBackground(true);
            Preferences.SetPulseGuardEnabled(true);
        }

        public ActivityLogService ActivityLog { get; }

        public UserPreferencesService Preferences { get; }

        public ThreatWatchBackgroundScanner CreateScanner(
            Func<CancellationToken, Task<ThreatWatchDetectionResult>> scanInvoker,
            TimeSpan? initialDelayOverride = null,
            TimeSpan? intervalOverride = null)
        {
            var initialDelay = initialDelayOverride ?? TimeSpan.FromMilliseconds(10);
            var interval = intervalOverride ?? TimeSpan.FromMilliseconds(50);
            return new ThreatWatchBackgroundScanner(
                scanService: null,
                activityLog: ActivityLog,
                preferences: Preferences,
                initialDelayOverride: initialDelay,
                scanIntervalOverride: interval,
                scanInvokerOverride: scanInvoker);
        }

        public ThreatWatchDetectionResult CreateResult(IReadOnlyList<SuspiciousProcessHit> hits)
        {
            return new ThreatWatchDetectionResult(
                Hits: hits,
                TotalProcesses: hits.Count,
                TrustedProcessCount: 0,
                WhitelistedCount: 0,
                StartupEntryCount: 0,
                HashLookupsPerformed: 0,
                ThreatIntelMatches: 0,
                CompletedAtUtc: DateTimeOffset.UtcNow);
        }

        public SuspiciousProcessHit CreateHit(string name, SuspicionLevel level)
        {
            return new SuspiciousProcessHit(
                id: $"hit-{Guid.NewGuid():N}",
                processName: name,
                filePath: $"C:/fake/{name}",
                level: level,
                matchedRules: new[] { "rule" },
                observedAtUtc: DateTimeOffset.UtcNow,
                hash: null,
                source: "tests",
                notes: null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", _previousLocalAppData);
            try
            {
                Directory.Delete(_tempLocalAppData, recursive: true);
            }
            catch
            {
            }
        }

        public Task<ActivityLogEntry> WaitForThreatWatchEntryAsync(TimeSpan timeout)
        {
            var signal = new TaskCompletionSource<ActivityLogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(object? sender, ActivityLogEventArgs args)
            {
                if (!string.Equals(args.Entry.Source, "Threat Watch", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ActivityLog.EntryAdded -= Handler;
                signal.TrySetResult(args.Entry);
            }

            ActivityLog.EntryAdded += Handler;
            return signal.Task.WaitAsync(timeout);
        }
    }
}
