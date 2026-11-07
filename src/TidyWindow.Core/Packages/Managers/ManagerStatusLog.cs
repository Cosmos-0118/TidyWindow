namespace TidyWindow.Next.Core;

/// <summary>Logs a package manager only when its condition changes, so routine refreshes stay silent.</summary>
public static class ManagerStatusLog
{
    public static void Record(ActivityLog log, ManagerStatus? before, ManagerStatus after)
    {
        var name = after.Definition.Name;
        // A newer release of the manager itself is news once, not on every refresh.
        if (after.Health == ManagerHealth.Ready && after.AvailableVersion is { Length: > 0 } available && before?.AvailableVersion != available)
            log.Write(ActivityLevel.Info, ActivityArea.Managers, $"{name} {available} is available", after.Version.Length > 0 ? "Installed: " + after.Version : null);
        if (before is not null && before.Health == after.Health && before.Problem == after.Problem) return;
        switch (after.Health)
        {
            case ManagerHealth.Ready:
                // A healthy manager on first look is the expected case, not news.
                if (before is not null)
                    log.Write(ActivityLevel.Success, ActivityArea.Managers, before.Health == ManagerHealth.Missing ? $"{name} is now installed" : $"{name} is working again", after.Version.Length > 0 ? "Version " + after.Version : null);
                break;
            case ManagerHealth.Missing:
                // Not having a particular manager is normal; losing one is not.
                if (before is not null) log.Write(ActivityLevel.Warning, ActivityArea.Managers, $"{name} was not found");
                break;
            default:
                var message = after.Health switch
                {
                    ManagerHealth.ConnectionIssue => $"{name} can't reach its package source",
                    ManagerHealth.Restricted => $"{name} is restricted by policy",
                    _ when after.Problem == ManagerProblem.Timeout => $"{name} did not respond in time",
                    _ => $"{name} needs repair"
                };
                log.Write(ActivityLevel.Warning, ActivityArea.Managers, message, after.Detail);
                break;
        }
    }
}
