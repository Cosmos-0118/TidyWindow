using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace TidyWindow.Core.Cleanup;

internal readonly struct CleanupCandidateScore
{
    public CleanupCandidateScore(bool shouldInclude, double confidence, long weight, IReadOnlyList<string> signals)
    {
        ShouldInclude = shouldInclude;
        Confidence = double.IsNaN(confidence) ? 0d : Math.Clamp(confidence, 0d, 1d);
        Weight = weight < 0 ? 0 : weight;
        Signals = signals ?? Array.Empty<string>();
    }

    public bool ShouldInclude { get; }

    public double Confidence { get; }

    public long Weight { get; }

    public IReadOnlyList<string> Signals { get; }
}

internal readonly struct CleanupFileContext
{
    public CleanupFileContext(
        string? name,
        string? fullPath,
        string? extension,
        long sizeBytes,
        DateTime lastModifiedUtc,
        bool isHidden,
        bool isSystem,
        bool wasRecentlyModified,
        DateTime lastAccessUtc = default,
        DateTime creationUtc = default,
        bool isLocked = false)
    {
        Name = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        FullPath = fullPath ?? string.Empty;
        Extension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension.Trim();
        SizeBytes = sizeBytes;
        LastModifiedUtc = lastModifiedUtc;
        IsHidden = isHidden;
        IsSystem = isSystem;
        WasRecentlyModified = wasRecentlyModified;
        LastAccessUtc = lastAccessUtc == default ? DateTime.MinValue : DateTime.SpecifyKind(lastAccessUtc, DateTimeKind.Utc);
        CreationUtc = creationUtc == default ? DateTime.MinValue : DateTime.SpecifyKind(creationUtc, DateTimeKind.Utc);
        IsLocked = isLocked;
    }

    public string Name { get; }

    public string FullPath { get; }

    public string Extension { get; }

    public long SizeBytes { get; }

    public DateTime LastModifiedUtc { get; }

    public DateTime LastAccessUtc { get; }

    public DateTime CreationUtc { get; }

    public bool IsHidden { get; }

    public bool IsSystem { get; }

    public bool WasRecentlyModified { get; }

    public bool IsLocked { get; }

    public CleanupFileContext WithLockState(bool isLocked)
    {
        if (isLocked == IsLocked)
        {
            return this;
        }

        return new CleanupFileContext(
            Name,
            FullPath,
            Extension,
            SizeBytes,
            LastModifiedUtc,
            IsHidden,
            IsSystem,
            WasRecentlyModified,
            LastAccessUtc,
            CreationUtc,
            isLocked);
    }
}

internal readonly struct CleanupDirectorySnapshot
{
    private static readonly IReadOnlyDictionary<string, int> EmptyExtensions = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(0, StringComparer.OrdinalIgnoreCase));

    public CleanupDirectorySnapshot(
        string? fullPath,
        string name,
        long sizeBytes,
        DateTime lastModifiedUtc,
        bool isHidden,
        bool isSystem,
        int fileCount,
        int hiddenFileCount,
        int systemFileCount,
        int recentFileCount,
        int tempFileCount,
        IReadOnlyDictionary<string, int>? extensionCounts)
    {
        FullPath = fullPath ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        SizeBytes = sizeBytes;
        LastModifiedUtc = lastModifiedUtc;
        IsHidden = isHidden;
        IsSystem = isSystem;
        FileCount = fileCount;
        HiddenFileCount = hiddenFileCount;
        SystemFileCount = systemFileCount;
        RecentFileCount = recentFileCount;
        TempFileCount = tempFileCount;
        ExtensionCounts = extensionCounts ?? EmptyExtensions;
    }

    public string FullPath { get; }

    public string Name { get; }

    public long SizeBytes { get; }

    public DateTime LastModifiedUtc { get; }

    public bool IsHidden { get; }

    public bool IsSystem { get; }

    public int FileCount { get; }

    public int HiddenFileCount { get; }

    public int SystemFileCount { get; }

    public int RecentFileCount { get; }

    public int TempFileCount { get; }

    public IReadOnlyDictionary<string, int> ExtensionCounts { get; }

    public double TempFileRatio => FileCount == 0 ? 0d : (double)TempFileCount / FileCount;

    public bool HasRecentFiles => RecentFileCount > 0;

    public bool IsEmpty => FileCount == 0 && SizeBytes == 0;
}

internal static class CleanupIntelligence
{
    private const double MaxScore = 1.5d;

    private static readonly HashSet<string> NoiseFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "thumbs.db",
        "ehthumbs.db",
        "iconcache.db",
        "fntcache.dat"
    };

    private static CleanupSignatureCatalog.SignatureSnapshot Signatures => CleanupSignatureCatalog.Snapshot;

    public static bool IsTempExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var normalized = NormalizeExtension(extension);
        return Signatures.TemporaryExtensions.Contains(normalized);
    }

    public static CleanupCandidateScore EvaluateFile(CleanupTargetDefinition definition, in CleanupFileContext context, DateTime referenceUtc)
    {
        if (context.IsLocked)
        {
            return new CleanupCandidateScore(false, 0d, 0, new[] { "Active handle detected" });
        }

        var signatures = Signatures;
        var signals = new List<string>(6);
        var score = 0d;
        var classification = definition.Classification ?? string.Empty;
        var normalizedClass = classification.Trim().ToLowerInvariant();
        if (normalizedClass is "temp" or "cache")
        {
            score += 0.15;
        }
        else if (normalizedClass == "logs")
        {
            score += 0.1;
        }
        else if (normalizedClass == "orphaned")
        {
            score += 0.1;
        }

        var sizeBytes = Math.Max(0, context.SizeBytes);
        if (sizeBytes >= 1_073_741_824)
        {
            score += 0.35;
            signals.Add("Large file (≥ 1 GB)");
        }
        else if (sizeBytes >= 524_288_000)
        {
            score += 0.25;
            signals.Add("Large file (≥ 512 MB)");
        }
        else if (sizeBytes >= 134_217_728)
        {
            score += 0.15;
            signals.Add("Large file (≥ 128 MB)");
        }

        var extension = NormalizeExtension(context.Extension);
        if (signatures.TemporaryExtensions.Contains(extension))
        {
            score += 0.35;
            signals.Add($"Temporary extension ({extension})");
        }
        else if (signatures.CrashDumpExtensions.Contains(extension))
        {
            score += 0.4;
            signals.Add("Crash dump artifact");
        }
        else if (signatures.PartialDownloadExtensions.Contains(extension))
        {
            score += 0.3;
            signals.Add("Incomplete download");
        }
        else if (signatures.LogExtensions.Contains(extension))
        {
            score += 0.25;
            signals.Add("Log file");
        }

        var name = context.Name;
        if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("~$", StringComparison.Ordinal))
        {
            score += 0.25;
            signals.Add("Temporary Office artifact");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var normalizedName = name.ToLowerInvariant();
            if (NoiseFileNames.Contains(normalizedName))
            {
                score += 0.2;
                signals.Add("Known redundant shell artifact");
            }

            if (signatures.ExactCrashFileNames.Contains(normalizedName))
            {
                score += 0.45;
                signals.Add("Recognized crash artifact");
            }
            else
            {
                foreach (var prefix in signatures.CrashFilePrefixes)
                {
                    if (normalizedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 0.2;
                        signals.Add($"Crash signature prefix ({prefix}*)");
                        break;
                    }
                }
            }

            if (normalizedName == "memory.dmp")
            {
                score += 0.4;
                signals.Add("Full memory dump");
            }

            if (normalizedName.EndsWith(".wer", StringComparison.OrdinalIgnoreCase))
            {
                score += 0.25;
                signals.Add("Windows Error Reporting package");
            }

            if (normalizedName.StartsWith("setup", StringComparison.OrdinalIgnoreCase) && extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
            {
                score += 0.2;
                signals.Add("Installer setup log");
            }
        }

        var normalizedFullPath = NormalizePath(context.FullPath);
        if (!string.IsNullOrWhiteSpace(normalizedFullPath))
        {
            foreach (var hint in signatures.CrashPathHints)
            {
                if (string.IsNullOrWhiteSpace(hint))
                {
                    continue;
                }

                if (normalizedFullPath.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 0.2;
                    signals.Add($"Crash path hint ({hint})");
                    break;
                }
            }
        }

        if (context.IsHidden)
        {
            score += 0.05;
            signals.Add("Hidden file");
        }

        if (context.IsSystem)
        {
            score -= 0.45;
            signals.Add("Marked as system file");
        }

        var ageDays = DaysBetween(context.LastModifiedUtc, referenceUtc);
        if (double.IsPositiveInfinity(ageDays))
        {
            score += 0.2;
            signals.Add("No timestamp information");
        }
        else if (ageDays < 0)
        {
            score += 0.1;
            signals.Add("Timestamp appears in the future");
        }
        else
        {
            if (ageDays >= 365)
            {
                score += 0.45;
                signals.Add("Inactive for over a year");
            }
            else if (ageDays >= 120)
            {
                score += 0.35;
                signals.Add("Inactive for 120+ days");
            }
            else if (ageDays >= 30)
            {
                score += 0.25;
                signals.Add("Inactive for 30+ days");
            }
            else if (ageDays >= 7)
            {
                score += 0.15;
                signals.Add("Inactive for 7+ days");
            }
            else if (ageDays < 1)
            {
                score -= 0.25;
                signals.Add("Modified within the last day");
            }
        }

        if (context.WasRecentlyModified)
        {
            score -= 0.2;
            signals.Add("Recently written");
        }

        if (normalizedClass == "logs" && ageDays >= 14)
        {
            score += 0.1;
            signals.Add("Stale log file");
        }

        if (context.LastAccessUtc != DateTime.MinValue)
        {
            var accessAge = DaysBetween(context.LastAccessUtc, referenceUtc);
            if (!double.IsPositiveInfinity(accessAge))
            {
                if (accessAge <= 1)
                {
                    score -= 0.2;
                    signals.Add("Accessed within the last day");
                }
                else if (accessAge <= 7)
                {
                    score -= 0.1;
                    signals.Add("Accessed within the last week");
                }
                else if (accessAge >= 180)
                {
                    score += 0.25;
                    signals.Add("No access in 180+ days");
                }
                else if (accessAge >= 60)
                {
                    score += 0.15;
                    signals.Add("No access in 60+ days");
                }
            }
        }

        if (context.CreationUtc != DateTime.MinValue)
        {
            var creationAge = DaysBetween(context.CreationUtc, referenceUtc);
            if (!double.IsPositiveInfinity(creationAge))
            {
                if (creationAge <= 3)
                {
                    score -= 0.15;
                    signals.Add("Recently created");
                }
                else if (creationAge >= 365)
                {
                    score += 0.1;
                    signals.Add("Created over a year ago");
                }
            }
        }

        score = Math.Clamp(score, 0d, MaxScore);
        var confidence = ConvertScoreToConfidence(score);

        var include = true;
        if (normalizedClass == "orphaned")
        {
            include = confidence >= 0.35 || signals.Count > 0 || signatures.TemporaryExtensions.Contains(extension) || signatures.PartialDownloadExtensions.Contains(extension) || signatures.CrashDumpExtensions.Contains(extension);
        }
        else if (normalizedClass == "logs")
        {
            include = sizeBytes > 4096 || confidence >= 0.25;
        }

        if (!include && sizeBytes >= 50_331_648)
        {
            include = true;
            signals.Add("Included because of large footprint");
            confidence = Math.Max(confidence, 0.35);
        }

        var weight = ComputeWeight(sizeBytes, confidence, isDirectoryCandidate: false);
        var finalSignals = signals.Count == 0
            ? Array.Empty<string>()
            : signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return new CleanupCandidateScore(include, confidence, weight, finalSignals);
    }

    public static CleanupCandidateScore EvaluateDirectory(CleanupTargetDefinition definition, in CleanupDirectorySnapshot snapshot, DateTime referenceUtc)
    {
        var signals = new List<string>(6);
        var score = 0.1;
        var classification = definition.Classification ?? string.Empty;
        var normalizedClass = classification.Trim().ToLowerInvariant();

        if (snapshot.IsHidden)
        {
            score += 0.05;
            signals.Add("Hidden directory");
        }

        if (snapshot.IsSystem)
        {
            score -= 0.45;
            signals.Add("Marked as system directory");
        }

        if (snapshot.IsEmpty)
        {
            score += 0.55;
            signals.Add("Empty directory");
        }

        if (snapshot.FileCount > 0)
        {
            var tempRatio = snapshot.TempFileRatio;
            if (tempRatio >= 0.95)
            {
                score += 0.35;
                signals.Add("Contains only temporary content");
            }
            else if (tempRatio >= 0.75)
            {
                score += 0.25;
                signals.Add("Mostly temporary content");
            }

            if (!snapshot.HasRecentFiles)
            {
                score += 0.25;
                signals.Add("No recent file activity");
            }

            if (snapshot.HiddenFileCount > 0 && snapshot.HiddenFileCount == snapshot.FileCount)
            {
                score += 0.1;
                signals.Add("All files are hidden");
            }
        }

        var sizeBytes = Math.Max(0, snapshot.SizeBytes);
        if (sizeBytes >= 2_147_483_648)
        {
            score += 0.4;
            signals.Add("Large directory (≥ 2 GB)");
        }
        else if (sizeBytes >= 536_870_912)
        {
            score += 0.25;
            signals.Add("Large directory (≥ 512 MB)");
        }
        else if (sizeBytes >= 134_217_728)
        {
            score += 0.15;
            signals.Add("Large directory (≥ 128 MB)");
        }

        var ageDays = DaysBetween(snapshot.LastModifiedUtc, referenceUtc);
        if (double.IsPositiveInfinity(ageDays))
        {
            score += 0.15;
            signals.Add("No timestamp information");
        }
        else if (ageDays < 0)
        {
            score += 0.1;
            signals.Add("Timestamp appears in the future");
        }
        else
        {
            if (ageDays >= 365)
            {
                score += 0.45;
                signals.Add("Inactive for over a year");
            }
            else if (ageDays >= 120)
            {
                score += 0.35;
                signals.Add("Inactive for 120+ days");
            }
            else if (ageDays >= 30)
            {
                score += 0.25;
                signals.Add("Inactive for 30+ days");
            }
            else if (ageDays >= 7)
            {
                score += 0.15;
                signals.Add("Inactive for 7+ days");
            }
            else if (ageDays < 1)
            {
                score -= 0.2;
                signals.Add("Modified within the last day");
            }
        }

        if (normalizedClass == "orphaned" && snapshot.FileCount == 0 && snapshot.SizeBytes == 0)
        {
            score += 0.2;
        }

        score = Math.Clamp(score, 0d, MaxScore);
        var confidence = ConvertScoreToConfidence(score);

        var include = snapshot.FileCount > 0 || snapshot.SizeBytes > 0 || snapshot.IsEmpty;
        if (normalizedClass == "orphaned")
        {
            include = confidence >= 0.25 || snapshot.TempFileCount > 0 || snapshot.IsEmpty;
        }

        var weight = ComputeWeight(sizeBytes, confidence, isDirectoryCandidate: true);
        var finalSignals = signals.Count == 0
            ? Array.Empty<string>()
            : signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return new CleanupCandidateScore(include, confidence, weight, finalSignals);
    }

    public static bool ShouldCheckActiveLock(CleanupTargetDefinition definition, in CleanupFileContext context)
    {
        if (context.IsLocked)
        {
            return false;
        }

        var normalizedClass = NormalizeClassification(definition.Classification);

        if (IsCrashArtifact(context))
        {
            return true;
        }

        if (context.SizeBytes >= 32L * 1024 * 1024)
        {
            return true;
        }

        if (normalizedClass == "logs" && context.WasRecentlyModified)
        {
            return true;
        }

        if (normalizedClass == "temp" && context.WasRecentlyModified && context.SizeBytes >= 4L * 1024 * 1024)
        {
            return true;
        }

        return false;
    }

    internal static bool IsCrashArtifact(in CleanupFileContext context)
    {
        var signatures = Signatures;
        var extension = NormalizeExtension(context.Extension);
        if (signatures.CrashDumpExtensions.Contains(extension))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(context.Name))
        {
            var normalizedName = context.Name.ToLowerInvariant();
            if (signatures.ExactCrashFileNames.Contains(normalizedName) || normalizedName.EndsWith(".wer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var prefix in signatures.CrashFilePrefixes)
            {
                if (normalizedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        var normalizedPath = NormalizePath(context.FullPath);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            foreach (var hint in signatures.CrashPathHints)
            {
                if (!string.IsNullOrWhiteSpace(hint) && normalizedPath.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static string GetCrashProductKey(in CleanupFileContext context)
    {
        var name = context.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var normalizedName = name.ToLowerInvariant();
        if (normalizedName == "memory.dmp")
        {
            return "system-memory";
        }

        var extension = NormalizeExtension(context.Extension);
        var signatures = Signatures;
        var isCrashExtension = signatures.CrashDumpExtensions.Contains(extension) || normalizedName.EndsWith(".wer", StringComparison.OrdinalIgnoreCase);
        if (!isCrashExtension)
        {
            return normalizedName;
        }

        var trimmed = normalizedName;
        if (!string.IsNullOrEmpty(extension) && trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^extension.Length];
        }

        if (trimmed.Length == 0)
        {
            return normalizedName;
        }

        var delimiters = new[] { '.', '-', '_', ' ' };
        var segments = trimmed.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment.All(static ch => char.IsDigit(ch)))
            {
                continue;
            }

            if (segment.Equals("dmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (segment.EndsWith("exe", StringComparison.OrdinalIgnoreCase))
            {
                return segment;
            }

            return segment;
        }

        return trimmed;
    }

    internal static DateTime GetMostRecentTimestamp(in CleanupFileContext context)
    {
        var candidate = context.LastModifiedUtc;
        if (context.LastAccessUtc > candidate)
        {
            candidate = context.LastAccessUtc;
        }

        if (context.CreationUtc > candidate)
        {
            candidate = context.CreationUtc;
        }

        return candidate;
    }

    private static string NormalizePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return string.Empty;
        }

        return fullPath.Replace("/", "\\");
    }

    private static long ComputeWeight(long sizeBytes, double confidence, bool isDirectoryCandidate)
    {
        sizeBytes = Math.Max(0, sizeBytes);
        var boost = (long)(confidence * 20_000_000); // up to 20 MB equivalent boost
        var multiplier = 0.65 + (confidence * 0.6);
        var scaled = (long)(sizeBytes * multiplier);
        var weight = Math.Max(sizeBytes, scaled);
        weight = Math.Max(weight, sizeBytes + boost);

        if (sizeBytes == 0 && isDirectoryCandidate && confidence >= 0.4)
        {
            weight = Math.Max(weight, boost + 5_000_000);
        }

        if (weight == 0 && confidence > 0)
        {
            weight = boost;
        }

        return weight;
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        if (!trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            trimmed = "." + trimmed;
        }

        return trimmed.ToLowerInvariant();
    }

    private static double ConvertScoreToConfidence(double score)
    {
        if (score <= 0)
        {
            return 0d;
        }

        var normalized = Math.Clamp(score / MaxScore, 0d, 1d);
        var centered = (normalized - 0.5d) * 6d;
        var logistic = Sigmoid(centered);
        return Math.Clamp(logistic, 0d, 1d);
    }

    private static double Sigmoid(double value)
    {
        return 1d / (1d + Math.Exp(-value));
    }

    private static string NormalizeClassification(string? classification)
    {
        return string.IsNullOrWhiteSpace(classification)
            ? string.Empty
            : classification.Trim().ToLowerInvariant();
    }

    private static double DaysBetween(DateTime timestampUtc, DateTime referenceUtc)
    {
        if (timestampUtc == DateTime.MinValue)
        {
            return double.PositiveInfinity;
        }

        var delta = referenceUtc - timestampUtc;
        return delta.TotalDays;
    }
}
