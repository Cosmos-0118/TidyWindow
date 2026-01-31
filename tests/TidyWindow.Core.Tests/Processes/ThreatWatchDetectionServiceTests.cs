using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Processes;
using TidyWindow.Core.Processes.ThreatWatch;
using Xunit;

namespace TidyWindow.Core.Tests.Processes;

public sealed class ThreatWatchDetectionServiceTests
{
    [Fact]
    public async Task FlagsCriticalProcessOutsideSystemDirectory()
    {
        var statePath = CreateTempPath();
        try
        {
            var store = new ProcessStateStore(statePath);
            var service = new ThreatWatchDetectionService(store);
            var process = new RunningProcessSnapshot(
                100,
                "lsass.exe",
                "C:/Users/Public/lsass.exe",
                commandLine: null,
                parentProcessId: null,
                parentProcessName: null,
                grandParentProcessId: null,
                grandParentProcessName: null,
                startedAtUtc: DateTimeOffset.UtcNow,
                isElevated: true);
            var request = new ThreatWatchDetectionRequest(new[] { process }, threatIntelMode: ThreatIntelMode.Disabled);

            var result = await service.RunScanAsync(request);

            Assert.Single(result.Hits);
            Assert.Equal(SuspicionLevel.Red, result.Hits[0].Level);
        }
        finally
        {
            DeleteIfExists(statePath);
        }
    }

    [Fact]
    public async Task SkipsTrustedDirectories()
    {
        var statePath = CreateTempPath();
        try
        {
            var store = new ProcessStateStore(statePath);
            var service = new ThreatWatchDetectionService(store);
            var trustedRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(trustedRoot))
            {
                trustedRoot = "C:/Program Files";
            }

            var process = new RunningProcessSnapshot(
                200,
                "code.exe",
                Path.Combine(trustedRoot, "VS Code", "Code.exe"),
                commandLine: null,
                parentProcessId: null,
                parentProcessName: null,
                grandParentProcessId: null,
                grandParentProcessName: null,
                startedAtUtc: DateTimeOffset.UtcNow,
                isElevated: false);
            var request = new ThreatWatchDetectionRequest(new[] { process }, threatIntelMode: ThreatIntelMode.Disabled);

            var result = await service.RunScanAsync(request);

            Assert.Empty(result.Hits);
        }
        finally
        {
            DeleteIfExists(statePath);
        }
    }

    [Fact]
    public async Task HonorsWhitelistBeforeEmittingHits()
    {
        var statePath = CreateTempPath();
        try
        {
            var store = new ProcessStateStore(statePath);
            store.UpsertWhitelistEntry(ThreatWatchWhitelistEntry.CreateDirectory("C:/Lab"));
            var service = new ThreatWatchDetectionService(store);
            var process = new RunningProcessSnapshot(
                300,
                "svchost.exe",
                "C:/Lab/svchost.exe",
                commandLine: null,
                parentProcessId: null,
                parentProcessName: null,
                grandParentProcessId: null,
                grandParentProcessName: null,
                startedAtUtc: DateTimeOffset.UtcNow,
                isElevated: false);
            var request = new ThreatWatchDetectionRequest(new[] { process }, threatIntelMode: ThreatIntelMode.Disabled);

            var result = await service.RunScanAsync(request);

            Assert.Empty(result.Hits);
        }
        finally
        {
            DeleteIfExists(statePath);
        }
    }

    [Fact]
    public async Task FlagsRandomTempExecutable()
    {
        var statePath = CreateTempPath();
        var tempExe = Path.Combine(Path.GetTempPath(), "AB12CD34.exe");
        try
        {
            var store = new ProcessStateStore(statePath);
            var service = new ThreatWatchDetectionService(store);
            File.WriteAllText(tempExe, "demo");
            var process = new RunningProcessSnapshot(
                400,
                "AB12CD34.exe",
                tempExe,
                commandLine: null,
                parentProcessId: null,
                parentProcessName: null,
                grandParentProcessId: null,
                grandParentProcessName: null,
                startedAtUtc: DateTimeOffset.UtcNow,
                isElevated: false);
            var request = new ThreatWatchDetectionRequest(new[] { process }, threatIntelMode: ThreatIntelMode.Disabled);

            var result = await service.RunScanAsync(request);

            Assert.Single(result.Hits);
            Assert.Equal(SuspicionLevel.Orange, result.Hits[0].Level);
        }
        finally
        {
            DeleteIfExists(statePath);
            DeleteIfExists(tempExe);
        }
    }

    [Fact]
    public async Task DetectsBlocklistedHash()
    {
        var statePath = CreateTempPath();
        var samplePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(samplePath, "malware");
            var hash = ComputeSha256(samplePath);
            var store = new ProcessStateStore(statePath);
            var provider = new StubThreatIntelProvider(hash);
            var service = new ThreatWatchDetectionService(store, new[] { provider });
            var process = new RunningProcessSnapshot(
                500,
                "bad.exe",
                samplePath,
                commandLine: null,
                parentProcessId: null,
                parentProcessName: null,
                grandParentProcessId: null,
                grandParentProcessName: null,
                startedAtUtc: DateTimeOffset.UtcNow,
                isElevated: false);
            var request = new ThreatWatchDetectionRequest(new[] { process }, threatIntelMode: ThreatIntelMode.LocalOnly);

            var result = await service.RunScanAsync(request);

            Assert.Single(result.Hits);
            Assert.Equal(SuspicionLevel.Red, result.Hits[0].Level);
        }
        finally
        {
            DeleteIfExists(statePath);
            DeleteIfExists(samplePath);
        }
    }

    [Fact]
    public async Task FlagsStartupEntriesInTempFolder()
    {
        var statePath = CreateTempPath();
        try
        {
            var store = new ProcessStateStore(statePath);
            var service = new ThreatWatchDetectionService(store);
            var startup = new StartupEntrySnapshot(
                "run!temp",
                "temp.exe",
                Path.Combine(Path.GetTempPath(), "temp.exe"),
                StartupEntryLocation.RunKey,
                arguments: null,
                source: "HKCU\\Run",
                description: "Temp autorun",
                isUnsigned: true);
            var request = new ThreatWatchDetectionRequest(Array.Empty<RunningProcessSnapshot>(), new[] { startup }, threatIntelMode: ThreatIntelMode.Disabled);

            var result = await service.RunScanAsync(request);

            Assert.Single(result.Hits);
            Assert.Equal("ThreatWatchDetection:Startup", result.Hits[0].Source);
        }
        finally
        {
            DeleteIfExists(statePath);
        }
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), $"TidyWindow_ThreatWatch_{Guid.NewGuid():N}.json");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class StubThreatIntelProvider : IThreatIntelProvider
    {
        private readonly string _hash;

        public StubThreatIntelProvider(string hash)
        {
            _hash = hash;
        }

        public ThreatIntelProviderKind Kind => ThreatIntelProviderKind.Local;

        public ValueTask<ThreatIntelResult> EvaluateAsync(string filePath, string? sha256, CancellationToken cancellationToken)
        {
            var normalized = sha256?.Trim().ToLowerInvariant();
            if (string.Equals(normalized, _hash, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(ThreatIntelResult.KnownBad(_hash, "stub"));
            }

            return ValueTask.FromResult(ThreatIntelResult.Unknown(sha256));
        }
    }
}
