using System;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Indicates the default action TidyWindow should take for a process.
/// </summary>
public enum ProcessActionPreference
{
    Keep = 0,
    AutoStop = 1
}

/// <summary>
/// High-level risk indicator for a catalog entry.
/// </summary>
public enum ProcessRiskLevel
{
    Safe = 0,
    Caution = 1,
    Critical = 2
}

/// <summary>
/// Specifies how a preference was derived.
/// </summary>
public enum ProcessPreferenceSource
{
    Unknown = 0,
    Questionnaire = 1,
    UserOverride = 2,
    SystemDefault = 3
}

/// <summary>
/// Discrete suspicion levels surfaced in the Threat Watch tab.
/// </summary>
public enum SuspicionLevel
{
    Green = 0,
    Yellow = 1,
    Orange = 2,
    Red = 3
}
