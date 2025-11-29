using System;
using System.Collections.Generic;

namespace TidyWindow.App.Services;

public enum AutomationWorkType
{
    Maintenance,
    Install,
    Cleanup,
    Essentials
}

public sealed record AutomationWorkItem(Guid Token, AutomationWorkType Type, string Description);

public interface IAutomationWorkTracker
{
    Guid BeginWork(AutomationWorkType type, string description);

    void CompleteWork(Guid token);

    bool HasActiveWork { get; }

    IReadOnlyList<AutomationWorkItem> GetActiveWork();

    event EventHandler? ActiveWorkChanged;
}
