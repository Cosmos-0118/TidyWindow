using Microsoft.UI.Xaml;

namespace TidyWindow.Next.Desktop;

/// <summary>A message that appears over the page, optionally with one button. It goes away on its own unless it has no time limit, and waits while it is being read.</summary>
public sealed class ToastItem : ObservableState
{
    private readonly Action<ToastItem> close;
    private Func<Task>? action;

    public ToastItem(string key, ToastKind kind, string title, string detail, string actionLabel, Func<Task>? action, Action<ToastItem> close, double seconds, Func<long> now)
    {
        Key = key; this.close = close; Kind = kind; Title = title; Detail = detail; ActionLabel = actionLabel; this.action = action;
        Countdown = new(TimeSpan.FromSeconds(seconds), now);
    }

    public string Key { get; }
    public ToastKind Kind { get; private set; }
    public string Title { get; private set; }
    public string Detail { get; private set; }
    public string ActionLabel { get; private set; }
    public Countdown Countdown { get; }
    /// <summary>How long the toast stays before it goes away on its own; 0 means until it is dismissed.</summary>
    public double Seconds => Countdown.Total.TotalSeconds;

    private int count = 1;
    /// <summary>How many times this same message has come up while it was still showing.</summary>
    public int Count { get => count; private set { if (Set(ref count, value)) { Changed(nameof(CountText)); Changed(nameof(CountVisibility)); } } }
    public string CountText => "×" + Count;
    public Visibility CountVisibility => Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    private bool closing;
    /// <summary>Set once the toast has been told to go; the card plays its exit and the toast is removed right after.</summary>
    public bool Closing { get => closing; internal set => Set(ref closing, value); }

    /// <summary>Raised when the message or its time was renewed, so the card can show it and restart its timer bar.</summary>
    public event Action<bool>? Renewed;
    /// <summary>Raised when the countdown stopped or carried on.</summary>
    public event Action? HoldChanged;

    public Visibility TimerVisibility => Seconds > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionVisibility => action is not null && ActionLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SuccessVisibility => Kind == ToastKind.Success ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningVisibility => Kind == ToastKind.Warning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InfoVisibility => Kind == ToastKind.Info ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Spoken by screen readers when the toast appears.</summary>
    public string Announcement => Detail.Length > 0 ? Title + ". " + Detail : Title;
    /// <summary>What copying puts on the clipboard.</summary>
    public string CopyText => Detail.Length > 0 ? Title + "\n\n" + Detail : Title;

    /// <summary>Stops or restarts the countdown for a reason (the pointer is over the toast, it is open for reading, the window is not in use).</summary>
    public void Hold(string reason, bool on) { if (Countdown.Hold(reason, on)) HoldChanged?.Invoke(); }

    /// <summary>The same message came up again, or the thing it is about changed: show the new text in the same toast and give it its full time again.</summary>
    internal void Renew(ToastKind kind, string title, string detail, string actionLabel, Func<Task>? run, double seconds)
    {
        var same = Kind == kind && Title == title && Detail == detail;
        Kind = kind; Title = title; Detail = detail; ActionLabel = actionLabel; action = run;
        if (same) Count++; else Count = 1;
        Countdown.Restart(TimeSpan.FromSeconds(seconds));
        foreach (var name in new[] { nameof(Kind), nameof(Title), nameof(Detail), nameof(ActionLabel), nameof(TimerVisibility), nameof(DetailVisibility), nameof(ActionVisibility), nameof(SuccessVisibility), nameof(WarningVisibility), nameof(InfoVisibility), nameof(Announcement), nameof(CopyText) })
            Changed(name);
        Renewed?.Invoke(same);
    }

    public void Dismiss() => close(this);

    public async Task RunAsync()
    {
        var run = action;
        close(this);
        if (run is not null) await run();
    }
}
