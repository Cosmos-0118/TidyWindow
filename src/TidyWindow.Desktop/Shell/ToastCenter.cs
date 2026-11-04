using System.Collections.ObjectModel;
using System.Diagnostics;
using TidyWindow.Next.Core;

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
    private readonly ActivityLog? log;
    private readonly CancellationTokenSource lifetime = new();
    private bool attentive = true;
    // How many toasts there are, as last counted on the UI thread: the timer thread must not read the collection itself.
    private volatile int showing;

    /// <param name="post">Runs something on the UI thread.</param>
    /// <param name="log">Where a failing toast action is recorded; without one it is only reported as a toast.</param>
    public ToastCenter(Action<Action> post, ActivityLog? log = null)
    {
        this.post = post; this.log = log;
        Items.CollectionChanged += (_, _) => showing = Items.Count;
        Loop = RunAsync();
    }

    public ObservableCollection<ToastItem> Items { get; } = [];

    /// <summary>The timer behind every countdown. Finished once the center is disposed.</summary>
    internal Task Loop { get; }

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
        var item = new ToastItem(key, kind, title, detail, action, run, Close, Failed, seconds, Clock);
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

    // A button on a message must not end the app when what it starts fails: the failure is recorded and reported like any other.
    private void Failed(ToastItem item, Exception ex)
    {
        log?.Write(ActivityLevel.Error, ActivityArea.App, $"“{item.ActionLabel}” could not finish", ex.ToString());
        Show("action-failed", ToastKind.Warning, "That could not be done", $"Something went wrong after “{item.ActionLabel}”. The details are in Logs.");
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
                if (showing == 0) continue;
                post(() => { foreach (var item in Items.Where(x => !x.Closing && x.Countdown.Expired).ToList()) Close(item); });
            }
        }
        catch (OperationCanceledException) { }
        // Only once the loop is done with the token: disposing it from Dispose, while the loop could be about to read it, threw.
        finally { lifetime.Dispose(); }
    }

    public void Dispose() => lifetime.Cancel();
}
