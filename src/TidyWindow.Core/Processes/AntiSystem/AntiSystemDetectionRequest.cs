using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TidyWindow.Core.Processes.AntiSystem;

/// <summary>
/// Configures a detection run, including the processes and startup entries to inspect.
/// </summary>
public sealed class AntiSystemDetectionRequest
{
    public AntiSystemDetectionRequest(
        IEnumerable<RunningProcessSnapshot> processes,
        IEnumerable<StartupEntrySnapshot>? startupEntries = null,
        bool includeBehaviorRules = true,
        bool includeStartupRules = true,
        bool recordFindings = true,
        int maxHashLookups = 32,
        ThreatIntelMode threatIntelMode = ThreatIntelMode.LocalOnly)
    {
        if (processes is null)
        {
            throw new ArgumentNullException(nameof(processes));
        }

        Processes = new ReadOnlyCollection<RunningProcessSnapshot>(processes.ToList());
        StartupEntries = startupEntries is null
            ? Array.Empty<StartupEntrySnapshot>()
            : new ReadOnlyCollection<StartupEntrySnapshot>(startupEntries.ToList());
        IncludeBehaviorRules = includeBehaviorRules;
        IncludeStartupRules = includeStartupRules;
        RecordFindings = recordFindings;
        MaxHashLookups = maxHashLookups <= 0 ? 0 : maxHashLookups;
        ThreatIntelMode = threatIntelMode;
    }

    public IReadOnlyList<RunningProcessSnapshot> Processes { get; }

    public IReadOnlyList<StartupEntrySnapshot> StartupEntries { get; }

    public bool IncludeBehaviorRules { get; }

    public bool IncludeStartupRules { get; }

    public bool RecordFindings { get; }

    public int MaxHashLookups { get; }

    public ThreatIntelMode ThreatIntelMode { get; }
}
