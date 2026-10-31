using Microsoft.UI.Xaml;

namespace TidyWindow.Next.Desktop;

public enum ToastKind { Info, Success, Warning }

/// <summary>A short message that appears over the page, optionally with one button, and goes away on its own unless it is a warning.</summary>
public sealed class ToastItem(string key, ToastKind kind, string title, string detail, string actionLabel, Func<Task>? action, Action<ToastItem> close, double seconds = 0) : ObservableState
{
    public string Key { get; } = key;
    public ToastKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string ActionLabel { get; } = actionLabel;
    /// <summary>How long the toast stays before it goes away on its own; 0 means until it is dismissed.</summary>
    public double Seconds { get; } = seconds;
    public Visibility TimerVisibility => Seconds > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionVisibility => action is not null && ActionLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SuccessVisibility => Kind == ToastKind.Success ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningVisibility => Kind == ToastKind.Warning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InfoVisibility => Kind == ToastKind.Info ? Visibility.Visible : Visibility.Collapsed;

    public void Dismiss() => close(this);

    public async Task RunAsync()
    {
        close(this);
        if (action is not null) await action();
    }
}
