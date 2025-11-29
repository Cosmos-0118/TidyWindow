using System;
using System.Collections.Generic;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class AutomationWorkTrackerTests
{
    [Fact]
    public void BeginAndCompleteWork_RaiseChangedEvents()
    {
        var tracker = new AutomationWorkTracker();
        var events = new List<bool>();
        tracker.ActiveWorkChanged += (_, _) => events.Add(tracker.HasActiveWork);

        var token = tracker.BeginWork(AutomationWorkType.Maintenance, "Test job");
        Assert.True(tracker.HasActiveWork);
        Assert.Contains(true, events);

        tracker.CompleteWork(token);
        Assert.False(tracker.HasActiveWork);
        Assert.Contains(false, events);
    }

    [Fact]
    public void GetActiveWork_ReturnsDescriptions()
    {
        var tracker = new AutomationWorkTracker();
        var token = tracker.BeginWork(AutomationWorkType.Install, "Install Visual Studio");

        var snapshot = tracker.GetActiveWork();
        var item = Assert.Single(snapshot);
        Assert.Equal(token, item.Token);
        Assert.Equal(AutomationWorkType.Install, item.Type);
        Assert.Equal("Install Visual Studio", item.Description);

        tracker.CompleteWork(token);
    }
}
