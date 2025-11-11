using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using TidyWindow.App.Services;

namespace TidyWindow.App.Tests;

internal sealed class TestTrayService : ITrayService
{
    private readonly TaskCompletionSource<PulseGuardNotification> _firstNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsExitRequested { get; private set; }

    public int HideToTrayCalls { get; private set; }

    public bool LastHideToTrayHint { get; private set; }

    public List<PulseGuardNotification> Notifications { get; } = new();

    public Window? AttachedWindow { get; private set; }

    public void Attach(Window window)
    {
        AttachedWindow = window;
    }

    public void ShowMainWindow()
    {
    }

    public void HideToTray(bool showHint)
    {
        HideToTrayCalls++;
        LastHideToTrayHint = showHint;
    }

    public void ShowNotification(PulseGuardNotification notification)
    {
        Notifications.Add(notification);
        _firstNotification.TrySetResult(notification);
    }

    public void PrepareForExit()
    {
        IsExitRequested = true;
    }

    public void ResetExitRequest()
    {
        IsExitRequested = false;
    }

    public void NavigateToLogs()
    {
    }

    public void Dispose()
    {
    }

    public void SetExitRequested(bool value)
    {
        IsExitRequested = value;
    }

    public Task<PulseGuardNotification> WaitForFirstNotificationAsync(TimeSpan timeout)
    {
        if (Notifications.Count > 0)
        {
            return Task.FromResult(Notifications[0]);
        }

        return _firstNotification.Task.WaitAsync(timeout);
    }
}

internal sealed class TestHighFrictionPromptService : IHighFrictionPromptService
{
    private readonly TaskCompletionSource<HighFrictionScenario> _scenarioSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void TryShowPrompt(HighFrictionScenario scenario, ActivityLogEntry entry)
    {
        if (scenario != HighFrictionScenario.None)
        {
            _scenarioSignal.TrySetResult(scenario);
        }
    }

    public Task<HighFrictionScenario> WaitForScenarioAsync(TimeSpan timeout)
    {
        return _scenarioSignal.Task.WaitAsync(timeout);
    }
}
