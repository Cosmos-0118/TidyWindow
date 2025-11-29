using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyWindow.App.Services;

public sealed class AutomationWorkTracker : IAutomationWorkTracker
{
    private readonly Dictionary<Guid, AutomationWorkItem> _active = new();
    private readonly object _lock = new();

    public event EventHandler? ActiveWorkChanged;

    public bool HasActiveWork
    {
        get
        {
            lock (_lock)
            {
                return _active.Count > 0;
            }
        }
    }

    public Guid BeginWork(AutomationWorkType type, string description)
    {
        var token = Guid.NewGuid();
        var normalized = string.IsNullOrWhiteSpace(description) ? "Automation task" : description.Trim();

        lock (_lock)
        {
            _active[token] = new AutomationWorkItem(token, type, normalized);
        }

        OnActiveWorkChanged();
        return token;
    }

    public void CompleteWork(Guid token)
    {
        if (token == Guid.Empty)
        {
            return;
        }

        var removed = false;
        lock (_lock)
        {
            removed = _active.Remove(token);
        }

        if (removed)
        {
            OnActiveWorkChanged();
        }
    }

    public IReadOnlyList<AutomationWorkItem> GetActiveWork()
    {
        lock (_lock)
        {
            return _active.Values.ToArray();
        }
    }

    private void OnActiveWorkChanged()
    {
        ActiveWorkChanged?.Invoke(this, EventArgs.Empty);
    }
}
