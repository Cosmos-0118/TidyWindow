using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Core.Cleanup;

internal static class CleanupSignatureCatalog
{
    private static readonly object SyncRoot = new();
    private static SignatureSnapshot _snapshot = SignatureSnapshot.CreateDefault();
    private static DateTime _lastRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    public static SignatureSnapshot Snapshot
    {
        get
        {
            EnsureSnapshot();
            return _snapshot;
        }
    }

    public static void ForceRefresh()
    {
        lock (SyncRoot)
        {
            _lastRefreshUtc = DateTime.MinValue;
            EnsureSnapshot(force: true);
        }
    }

    private static void EnsureSnapshot(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastRefreshUtc < RefreshInterval)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (!force && now - _lastRefreshUtc < RefreshInterval)
            {
                return;
            }

            var snapshot = SignatureSnapshot.CreateDefault();
            try
            {
                var path = ResolveConfigPath();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var config = JsonSerializer.Deserialize<CleanupSignatureConfig>(json, SerializerOptions);
                    if (config is not null)
                    {
                        snapshot = snapshot.FromConfig(config);
                    }
                }
            }
            catch
            {
                // Ignore load failures; fallback to default snapshot.
            }

            _snapshot = snapshot;
            _lastRefreshUtc = now;
        }
    }

    private static string? ResolveConfigPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("TIDYWINDOW_CLEANUP_SIGNATURE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                var candidate = Path.Combine(baseDirectory, "data", "cleanup", "signatures.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Ignore path resolution issues and rely on defaults.
        }

        return null;
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    internal readonly record struct SignatureSnapshot(
        IReadOnlySet<string> TemporaryExtensions,
        IReadOnlySet<string> CrashDumpExtensions,
        IReadOnlySet<string> PartialDownloadExtensions,
        IReadOnlySet<string> LogExtensions,
        IReadOnlySet<string> ExactCrashFileNames,
        IReadOnlySet<string> CrashFilePrefixes,
        IReadOnlyList<string> CrashPathHints,
        int CrashDumpNewestRetentionCount)
    {
        public static SignatureSnapshot CreateDefault()
        {
            return new SignatureSnapshot(
                BuildSet(
                    ".tmp", ".temp", ".bak", ".old", ".chk", "._mp", ".partial", ".part", ".cache", ".copy", ".swo", ".swp", ".tmpx", ".~", ".tmp1", ".tmp2", ".tmp3", ".tmp4", ".gsd", ".sbstore"),
                BuildSet(
                    ".dmp", ".mdmp", ".hdmp", ".wer", ".mdmp2"),
                BuildSet(
                    ".crdownload", ".download", ".opdownload", ".ucas", ".aria2", ".part", ".partial", ".tmpdownload"),
                BuildSet(
                    ".log", ".etl", ".evtx", ".wrn", ".txtlog"),
                BuildSet(
                    "memory.dmp", "setupact.log", "setuperr.log", "windowsupdate.log"),
                BuildSet(
                    "setup", "crash", "wer"),
                new[]
                {
                    "\\AppData\\Local\\CrashDumps",
                    "\\AppData\\Local\\Microsoft\\Windows\\WER",
                    "\\ProgramData\\Microsoft\\Windows\\WER",
                    "\\Windows\\Minidump",
                    "\\ProgramData\\Package Cache",
                    "\\Windows\\Logs\\WindowsUpdate"
                },
                2);
        }

        public SignatureSnapshot WithTemporaryExtensions(IReadOnlySet<string> extensions)
            => this with { TemporaryExtensions = extensions };

        public SignatureSnapshot WithCrashDumpExtensions(IReadOnlySet<string> extensions)
            => this with { CrashDumpExtensions = extensions };

        public SignatureSnapshot WithPartialDownloadExtensions(IReadOnlySet<string> extensions)
            => this with { PartialDownloadExtensions = extensions };

        public SignatureSnapshot WithLogExtensions(IReadOnlySet<string> extensions)
            => this with { LogExtensions = extensions };

        public SignatureSnapshot WithExactCrashFileNames(IReadOnlySet<string> names)
            => this with { ExactCrashFileNames = names };

        public SignatureSnapshot WithCrashFilePrefixes(IReadOnlySet<string> prefixes)
            => this with { CrashFilePrefixes = prefixes };

        public SignatureSnapshot WithCrashPathHints(IReadOnlyList<string> hints)
            => this with { CrashPathHints = hints };

        public SignatureSnapshot WithCrashDumpNewestRetentionCount(int count)
            => this with { CrashDumpNewestRetentionCount = count };

        private static IReadOnlySet<string> BuildSet(params string[] values)
        {
            if (values is null || values.Length == 0)
            {
                return ImmutableHashSet<string>.Empty;
            }

            return values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Where(static value => value.Length > 0)
                .Select(static value => value.ToLowerInvariant())
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class CleanupSignatureConfig
    {
        public string[]? TemporaryExtensions { get; set; }

        public string[]? CrashDumpExtensions { get; set; }

        public string[]? PartialDownloadExtensions { get; set; }

        public string[]? LogExtensions { get; set; }

        public string[]? ExactCrashFileNames { get; set; }

        public string[]? CrashFilePrefixes { get; set; }

        public string[]? CrashPathHints { get; set; }

        public int? CrashDumpNewestRetentionCount { get; set; }
    }

    private static SignatureSnapshot FromConfig(this SignatureSnapshot fallback, CleanupSignatureConfig config)
    {
        var snapshot = fallback;

        if (TryBuildSet(config.TemporaryExtensions, out var tempExtensions))
        {
            snapshot = snapshot.WithTemporaryExtensions(tempExtensions);
        }

        if (TryBuildSet(config.CrashDumpExtensions, out var crashExtensions))
        {
            snapshot = snapshot.WithCrashDumpExtensions(crashExtensions);
        }

        if (TryBuildSet(config.PartialDownloadExtensions, out var partialExtensions))
        {
            snapshot = snapshot.WithPartialDownloadExtensions(partialExtensions);
        }

        if (TryBuildSet(config.LogExtensions, out var logExtensions))
        {
            snapshot = snapshot.WithLogExtensions(logExtensions);
        }

        if (TryBuildSet(config.ExactCrashFileNames, out var exactNames))
        {
            snapshot = snapshot.WithExactCrashFileNames(exactNames);
        }

        if (TryBuildSet(config.CrashFilePrefixes, out var prefixes))
        {
            snapshot = snapshot.WithCrashFilePrefixes(prefixes);
        }

        if (config.CrashPathHints is { Length: > 0 })
        {
            snapshot = snapshot.WithCrashPathHints(
                config.CrashPathHints
                    .Where(static hint => !string.IsNullOrWhiteSpace(hint))
                    .Select(static hint => hint.Trim())
                    .Where(static hint => hint.Length > 0)
                    .ToArray());
        }

        if (config.CrashDumpNewestRetentionCount is int count && count >= 0)
        {
            snapshot = snapshot.WithCrashDumpNewestRetentionCount(count);
        }

        return snapshot;
    }

    private static bool TryBuildSet(string[]? values, out IReadOnlySet<string> set)
    {
        if (values is null || values.Length == 0)
        {
            set = ImmutableHashSet<string>.Empty;
            return false;
        }

        set = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .Select(static value => value.ToLowerInvariant())
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count > 0;
    }
}
