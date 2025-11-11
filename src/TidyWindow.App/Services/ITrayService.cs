using System;
using System.Windows;

namespace TidyWindow.App.Services;

public interface ITrayService : IDisposable
{
    bool IsExitRequested { get; }

    void Attach(Window window);

    void ShowMainWindow();

    void HideToTray(bool showHint);

    void ShowNotification(PulseGuardNotification notification);

    void PrepareForExit();

    void ResetExitRequest();

    void NavigateToLogs();
}
