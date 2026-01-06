using System;
using System.Collections.Generic;

namespace TidyWindow.Core.Processes.ThreatWatch;

/// <summary>
/// Summary of a detection pass including surfaced hits and aggregate stats.
/// </summary>
public sealed record ThreatWatchDetectionResult(
    IReadOnlyList<SuspiciousProcessHit> Hits,
    int TotalProcesses,
    int TrustedProcessCount,
    int WhitelistedCount,
    int StartupEntryCount,
    int HashLookupsPerformed,
    int ThreatIntelMatches,
    DateTimeOffset CompletedAtUtc);
