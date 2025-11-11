using System;

namespace TidyWindow.App.Services;

public sealed record PulseGuardNotification(
    PulseGuardNotificationKind Kind,
    string Title,
    string Message,
    ActivityLogEntry Entry,
    bool NavigateToLogs = true,
    DateTimeOffset? TimestampOverride = null)
{
    public DateTimeOffset Timestamp => TimestampOverride ?? Entry.Timestamp;
}

public enum PulseGuardNotificationKind
{
    SuccessDigest,
    Insight,
    ActionRequired
}
