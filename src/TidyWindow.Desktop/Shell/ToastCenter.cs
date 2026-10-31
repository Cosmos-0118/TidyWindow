using System.Collections.ObjectModel;
using System.Diagnostics;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// The one place every page reports to. Messages appear as toasts over the page, so nothing on the page moves, and they go away by themselves
/// unless they are about something the person still has to deal with. A toast waits while it is being read, a message that comes up again
/// updates the toast already showing, and a long one is shortened until the person opens it.
/// </summary>
public sealed class ToastCenter : IDisposable
{
    private const int Limit = 4;
    /// <summary>How long a toast keeps playing its exit before it is taken away.</summary>
    internal static readonly TimeSpan ExitTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private readonly Action<Action> post;
    private readonly CancellationTokenSource lifetime = new();
    private bool attentive = true;

    /// <param name="post">Runs something on the UI thread.</param>
    public ToastCenter(Action<Action> post)
    {
        this.post = post;
        _ = RunAsync();
    }

    public ObservableCollection<ToastItem> Items { get; } = [];

    /// <summary>False while the window is not in use. Toasts wait then, so one cannot come and go unseen.</summary>
    public bool Attentive
    {
        get => attentive;
        set { attentive = value; post(() => { foreach (var item in Items) item.Hold("away", !value); }); }
    }

    /// <param name="seconds">How long it stays. Negative works it out from the kind and how much there is to read; 0 keeps it until it is dismissed.</param>
    public void Show(string key, ToastKind kind, string title, string detail = "", string action = "", Func<Task>? run = null, double seconds = -1)
    {
        if (lifetime.IsCancellationRequested) return;
        title = ToastPolicy.Clamp(title, ToastPolicy.MaxTitle);
        detail = ToastPolicy.Clamp(detail, ToastPolicy.MaxDetail);
        seconds = ToastPolicy.Seconds(kind, title, detail, seconds);
        // A newer message about the same thing updates the toast that is there instead of stacking up or flickering out and in.
        if (Items.FirstOrDefault(x => x.Key == key && !x.Closing) is { } existing)
        {
            existing.Renew(kind, title, detail, action, run, seconds);
            return;
        }
        var item = new ToastItem(key, kind, title, detail, action, run, Close, seconds, Clock);
        if (!attentive) item.Hold("away", true);
        Items.Add(item);
        MakeRoom();
    }

    /// <summary>Takes a toast away with its exit animation.</summary>
    public void Close(ToastItem item)
    {
        if (item.Closing || lifetime.IsCancellationRequested) return;
        item.Closing = true;
        _ = Task.Delay(ExitTime, lifetime.Token).ContinueWith(_ => post(() => Items.Remove(item)), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public void Remove(string key) => RemoveWhere(x => x == key);

    public void RemoveWhere(Func<string, bool> match)
    {
        foreach (var old in Items.Where(x => !x.Closing && match(x.Key)).ToList()) Close(old);
    }

    public bool Has(Func<string, bool> match) => Items.Any(x => !x.Closing && match(x.Key));

    /// <summary>The first lines of a long message, so a toast stays short; the full text is in Logs.</summary>
    public static string Brief(string text, int max = 240) => ToastPolicy.Brief(text, max);

    // Past the limit the oldest goes, but one that will leave by itself is let go before one the person has to deal with.
    private void MakeRoom()
    {
        while (Items.Count(x => !x.Closing) > Limit)
        {
            var live = Items.Where(x => !x.Closing).ToList();
            Close(live.FirstOrDefault(x => !x.Countdown.Endless) ?? live[0]);
        }
    }

    private static long Clock() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    // One timer for all toasts: whichever have run out are closed.
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (Items.Count == 0) continue;
                post(() => { foreach (var item in Items.Where(x => !x.Closing && x.Countdown.Expired).ToList()) Close(item); });
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
