using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Processes.AntiSystem;

/// <summary>
/// Implements the four-layer Anti-System detection pipeline.
/// </summary>
public sealed class AntiSystemDetectionService
{
    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "smss.exe",
        "csrss.exe",
        "wininit.exe",
        "services.exe",
        "lsass.exe",
        "lsm.exe",
        "svchost.exe",
        "winlogon.exe",
        "explorer.exe",
        "spoolsv.exe",
        "dwm.exe",
        "ctfmon.exe",
        "fontdrvhost.exe",
        "sihost.exe",
        "runtimebroker.exe",
        "taskhostw.exe",
        "searchapp.exe",
        "system",
        "system idle process"
    };

    private readonly ProcessStateStore _stateStore;
    private readonly IReadOnlyList<IThreatIntelProvider> _threatIntelProviders;
    private readonly string[] _trustedRoots;
    private readonly string[] _systemRoots;
    private readonly string[] _tempRoots;
    private readonly string? _userProfileRoot;
    private readonly string? _downloadsRoot;

    public AntiSystemDetectionService(ProcessStateStore stateStore, IEnumerable<IThreatIntelProvider>? threatIntelProviders = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _threatIntelProviders = threatIntelProviders?.Where(static provider => provider is not null).ToArray() ?? Array.Empty<IThreatIntelProvider>();
        _trustedRoots = BuildTrustedRoots();
        _systemRoots = BuildSystemRoots();
        _tempRoots = BuildTempRoots();
        _userProfileRoot = NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _downloadsRoot = string.IsNullOrWhiteSpace(_userProfileRoot)
            ? null
            : NormalizeDirectory(Path.Combine(_userProfileRoot, "Downloads"));
    }

    public async Task<AntiSystemDetectionResult> RunScanAsync(AntiSystemDetectionRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var whitelist = _stateStore.GetWhitelistEntries();
        var hits = new List<SuspiciousProcessHit>();
        var trustedCount = 0;
        var whitelistCount = 0;
        var hashLookups = 0;
        var intelMatches = 0;
        var providers = SelectProviders(request.ThreatIntelMode);

        foreach (var process in request.Processes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedPath = NormalizeFilePath(process.FilePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            if (IsUnderDirectory(normalizedPath, _trustedRoots))
            {
                trustedCount++;
                continue;
            }

            if (MatchesWhitelist(whitelist, normalizedPath, null, process.ProcessName, out _))
            {
                whitelistCount++;
                continue;
            }

            var ruleMatches = new List<DetectionRuleMatch>();
            EvaluateCriticalProcessLayer(process, normalizedPath, ruleMatches);
            if (request.IncludeBehaviorRules)
            {
                EvaluateBehaviorLayer(process, normalizedPath, ruleMatches);
                EvaluateUserLocationLayer(normalizedPath, ruleMatches);
            }

            string? sha256 = null;
            var intelResult = ThreatIntelResult.Unknown(null);
            if (providers.Count > 0 && hashLookups < request.MaxHashLookups && File.Exists(normalizedPath))
            {
                sha256 = FileHashing.TryComputeSha256(normalizedPath);
                if (!string.IsNullOrWhiteSpace(sha256))
                {
                    hashLookups++;
                    if (MatchesWhitelist(whitelist, normalizedPath, sha256, process.ProcessName, out _))
                    {
                        whitelistCount++;
                        continue;
                    }

                    intelResult = await QueryThreatIntelAsync(providers, normalizedPath, sha256, cancellationToken).ConfigureAwait(false);
                    if (intelResult.Verdict == ThreatIntelVerdict.KnownBad)
                    {
                        intelMatches++;
                        AddRule(ruleMatches, DetectionRuleMatch.Red(intelResult.Source ?? "threatintel.match"));
                    }
                }
            }

            if (ruleMatches.Count == 0)
            {
                continue;
            }

            var level = ResolveLevel(ruleMatches);
            if (level == SuspicionLevel.Green)
            {
                continue;
            }

            var hit = new SuspiciousProcessHit(
                CreateDeterministicId("proc", normalizedPath, process.ProcessId, sha256),
                process.ProcessName,
                normalizedPath,
                level,
                ruleMatches.ConvertAll(static match => match.RuleId),
                DateTimeOffset.UtcNow,
                intelResult.Sha256 ?? sha256,
                "AntiSystemDetection:Process",
                BuildNotes(process, intelResult));

            hits.Add(hit);
            if (request.RecordFindings)
            {
                _stateStore.RecordSuspiciousHit(hit);
            }
        }

        if (request.IncludeStartupRules && request.StartupEntries.Count > 0)
        {
            EvaluateStartupLayer(request, whitelist, hits, ref whitelistCount);
        }

        return new AntiSystemDetectionResult(
            hits,
            request.Processes.Count,
            trustedCount,
            whitelistCount,
            request.StartupEntries.Count,
            hashLookups,
            intelMatches,
            DateTimeOffset.UtcNow);
    }

    private void EvaluateStartupLayer(AntiSystemDetectionRequest request, IReadOnlyCollection<AntiSystemWhitelistEntry> whitelist, List<SuspiciousProcessHit> hits, ref int whitelistCount)
    {
        foreach (var entry in request.StartupEntries)
        {
            var normalizedPath = NormalizeFilePath(entry.ExecutablePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            if (IsUnderDirectory(normalizedPath, _trustedRoots))
            {
                continue;
            }

            if (MatchesWhitelist(whitelist, normalizedPath, null, entry.ProcessName, out _))
            {
                whitelistCount++;
                continue;
            }

            var matches = new List<DetectionRuleMatch>();
            if (IsUnderDirectory(normalizedPath, _tempRoots))
            {
                AddRule(matches, DetectionRuleMatch.Red("startup.temp-exe"));
            }
            else if (IsInUserWorkspace(normalizedPath))
            {
                AddRule(matches, DetectionRuleMatch.Yellow("startup.user-location"));
            }

            if (matches.Count == 0)
            {
                continue;
            }

            var hit = new SuspiciousProcessHit(
                CreateDeterministicId("startup", normalizedPath, entry.EntryId.GetHashCode(StringComparison.OrdinalIgnoreCase), null, entry.EntryId),
                entry.ProcessName,
                normalizedPath,
                ResolveLevel(matches),
                matches.ConvertAll(static match => match.RuleId),
                DateTimeOffset.UtcNow,
                hash: null,
                source: "AntiSystemDetection:Startup",
                entry.Description ?? entry.Source);

            hits.Add(hit);
            if (request.RecordFindings)
            {
                _stateStore.RecordSuspiciousHit(hit);
            }
        }
    }

    private void EvaluateCriticalProcessLayer(RunningProcessSnapshot process, string normalizedPath, List<DetectionRuleMatch> matches)
    {
        if (!CriticalProcesses.Contains(process.ProcessName))
        {
            return;
        }

        if (!IsUnderDirectory(normalizedPath, _systemRoots))
        {
            AddRule(matches, DetectionRuleMatch.Red("critical-process-path"));
        }
    }

    private void EvaluateBehaviorLayer(RunningProcessSnapshot process, string normalizedPath, List<DetectionRuleMatch> matches)
    {
        var name = process.ProcessName.ToLowerInvariant();
        var fileName = Path.GetFileName(normalizedPath).ToLowerInvariant();

        if (name == "svchost.exe" && !IsUnderDirectory(normalizedPath, _systemRoots))
        {
            AddRule(matches, DetectionRuleMatch.Orange("behavior.svchost-outside-system32"));
        }
        else if (fileName.Contains("svchost", StringComparison.Ordinal) && normalizedPath.Contains("appdata", StringComparison.OrdinalIgnoreCase))
        {
            AddRule(matches, DetectionRuleMatch.Orange("behavior.svchost-appdata"));
        }

        if (name == "net.exe" && process.ParentProcessName == "cmd.exe" && process.GrandParentProcessName == "explorer.exe")
        {
            AddRule(matches, DetectionRuleMatch.Orange("behavior.explorer-cmd-net"));
        }

        if (IsUnderDirectory(normalizedPath, _tempRoots) && LooksRandomExecutable(fileName))
        {
            AddRule(matches, DetectionRuleMatch.Orange("behavior.temp-random-exe"));
        }
    }

    private void EvaluateUserLocationLayer(string normalizedPath, List<DetectionRuleMatch> matches)
    {
        if (IsInUserWorkspace(normalizedPath) || (!string.IsNullOrWhiteSpace(_downloadsRoot) && IsUnderDirectory(normalizedPath, _downloadsRoot)))
        {
            AddRule(matches, DetectionRuleMatch.Yellow("user-location-unsigned"));
        }
    }

    private IReadOnlyList<IThreatIntelProvider> SelectProviders(ThreatIntelMode mode)
    {
        if (mode == ThreatIntelMode.Disabled || _threatIntelProviders.Count == 0)
        {
            return Array.Empty<IThreatIntelProvider>();
        }

        if (mode == ThreatIntelMode.Full)
        {
            return _threatIntelProviders;
        }

        return _threatIntelProviders
            .Where(static provider => provider.Kind == ThreatIntelProviderKind.Local)
            .ToArray();
    }

    private static async ValueTask<ThreatIntelResult> QueryThreatIntelAsync(IReadOnlyList<IThreatIntelProvider> providers, string filePath, string? sha256, CancellationToken cancellationToken)
    {
        ThreatIntelResult lastResult = ThreatIntelResult.Unknown(sha256);
        foreach (var provider in providers)
        {
            var verdict = await provider.EvaluateAsync(filePath, sha256, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(verdict.Sha256))
            {
                sha256 = verdict.Sha256;
            }

            if (verdict.Verdict != ThreatIntelVerdict.Unknown)
            {
                return verdict;
            }

            lastResult = verdict;
        }

        return lastResult with { Sha256 = sha256 };
    }

    private static SuspicionLevel ResolveLevel(List<DetectionRuleMatch> matches)
    {
        if (matches.Any(static match => match.Level == SuspicionLevel.Red))
        {
            return SuspicionLevel.Red;
        }

        if (matches.Any(static match => match.Level == SuspicionLevel.Orange))
        {
            return SuspicionLevel.Orange;
        }

        if (matches.Any(static match => match.Level == SuspicionLevel.Yellow))
        {
            return SuspicionLevel.Yellow;
        }

        return SuspicionLevel.Green;
    }

    private static string BuildNotes(RunningProcessSnapshot process, ThreatIntelResult intelResult)
    {
        var builder = new StringBuilder();
        builder.Append("PID ").Append(process.ProcessId);
        if (!string.IsNullOrWhiteSpace(process.CommandLine))
        {
            builder.Append(" | cmd: ").Append(process.CommandLine);
        }

        if (intelResult.Verdict == ThreatIntelVerdict.KnownBad && !string.IsNullOrWhiteSpace(intelResult.Source))
        {
            builder.Append(" | source: ").Append(intelResult.Source);
        }

        return builder.ToString();
    }

    private static void AddRule(List<DetectionRuleMatch> matches, DetectionRuleMatch match)
    {
        if (matches.Any(existing => string.Equals(existing.RuleId, match.RuleId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        matches.Add(match);
    }

    private bool IsInUserWorkspace(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(_userProfileRoot))
        {
            return false;
        }

        if (!IsUnderDirectory(normalizedPath, _userProfileRoot))
        {
            return false;
        }

        return !normalizedPath.Contains("appdata", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var normalized = Path.GetFullPath(path.Trim());
            return normalized.EndsWith(Path.DirectorySeparatorChar) || normalized.EndsWith(Path.AltDirectorySeparatorChar)
                ? normalized
                : normalized + Path.DirectorySeparatorChar;
        }
        catch
        {
            var sanitized = path.Trim();
            if (!sanitized.EndsWith(Path.DirectorySeparatorChar) && !sanitized.EndsWith(Path.AltDirectorySeparatorChar))
            {
                sanitized += Path.DirectorySeparatorChar;
            }

            return sanitized;
        }
    }

    private static bool IsUnderDirectory(string candidatePath, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return candidatePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderDirectory(string candidatePath, IReadOnlyList<string> roots)
    {
        if (roots is null || roots.Count == 0)
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (IsUnderDirectory(candidatePath, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksRandomExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^4];
        if (stem.Length != 8)
        {
            return false;
        }

        for (var i = 0; i < stem.Length; i++)
        {
            if (!char.IsLetterOrDigit(stem[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesWhitelist(IReadOnlyCollection<AntiSystemWhitelistEntry> entries, string? filePath, string? sha256, string? processName, out AntiSystemWhitelistEntry? match)
    {
        if (entries is null || entries.Count == 0)
        {
            match = null;
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.Matches(filePath, sha256, processName))
            {
                match = entry;
                return true;
            }
        }

        match = null;
        return false;
    }

    private static string CreateDeterministicId(string scope, string normalizedPath, int numericToken, string? sha256, string? seed = null)
    {
        var payload = string.Join("|", scope, normalizedPath, numericToken.ToString(), sha256 ?? string.Empty, seed ?? string.Empty);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string[] BuildTrustedRoots()
    {
        var roots = new List<string>();
        AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddIfPresent(roots, Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
        AddIfPresent(roots, Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHub"));
        AddIfPresent(roots, Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"));
        AddIfPresent(roots, Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git"));
        AddIfPresent(roots, Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code"));
        AddIfPresent(roots, Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"));
        AddIfPresent(roots, Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildSystemRoots()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = new List<string>
        {
            NormalizeDirectory(windows),
            NormalizeDirectory(Combine(windows, "System32")),
            NormalizeDirectory(Combine(windows, "SysWOW64"))
        };

        return list.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildTempRoots()
    {
        var roots = new List<string>
        {
            NormalizeDirectory(Path.GetTempPath()),
            NormalizeDirectory(Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"))
        };

        return roots.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? Combine(string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var filtered = segments.Where(static segment => !string.IsNullOrWhiteSpace(segment)).ToArray();
        if (filtered.Length == 0)
        {
            return root;
        }

        return Path.Combine(root, Path.Combine(filtered));
    }

    private static void AddIfPresent(List<string> roots, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var normalized = NormalizeDirectory(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                roots.Add(normalized);
            }
        }
    }

    private readonly record struct DetectionRuleMatch(string RuleId, SuspicionLevel Level)
    {
        public static DetectionRuleMatch Red(string ruleId) => new(ruleId, SuspicionLevel.Red);

        public static DetectionRuleMatch Orange(string ruleId) => new(ruleId, SuspicionLevel.Orange);

        public static DetectionRuleMatch Yellow(string ruleId) => new(ruleId, SuspicionLevel.Yellow);
    }
}
