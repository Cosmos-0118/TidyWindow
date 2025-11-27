using System;
using System.Collections.Generic;
using TidyWindow.Core.Uninstall;

namespace TidyWindow.App.Services.Cleanup;

public sealed class AppCleanupPlan
{
    public AppCleanupPlan(InstalledApp app, IReadOnlyList<CleanupSuggestion> suggestions, IReadOnlyList<string> deferredItems)
    {
        App = app ?? throw new ArgumentNullException(nameof(app));
        Suggestions = suggestions ?? Array.Empty<CleanupSuggestion>();
        DeferredItems = deferredItems ?? Array.Empty<string>();
    }

    public InstalledApp App { get; }

    public IReadOnlyList<CleanupSuggestion> Suggestions { get; }

    public IReadOnlyList<string> DeferredItems { get; }

    public bool HasSuggestions => Suggestions.Count > 0;
}
