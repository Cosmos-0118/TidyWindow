using System;

namespace TidyWindow.Core.Startup;

public sealed record StartupDelayPlan(
    string Id,
    StartupItemSourceKind SourceKind,
    string ReplacementTaskPath,
    int DelaySeconds,
    DateTimeOffset CreatedAtUtc);
