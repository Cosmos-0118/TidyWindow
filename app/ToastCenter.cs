using System.Collections.ObjectModel;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// The one place every page reports to. Messages appear as toasts over the page, so nothing on the page moves, and they go away by themselves
/// unless they are about something the person still has to deal with.
/// </summary>
public sealed class ToastCenter(Action<Action> post) : IDisposable
{
    private const int Limit = 4;
    private readonly CancellationTokenSource lifetime = new();

    public ObservableCollection<ToastItem> Items { get; } = [];

    /// <param name="seconds">How long it stays. Negative picks a default by kind; 0 keeps it until it is dismissed.</param>
    public void Show(string key, ToastKind kind, string title, string detail = "", string action = "", Func<Task>? run = null, double seconds = -1)
    {
        if (lifetime.IsCancellationRequested) return;
        // A newer message about the same thing replaces the older one instead of stacking up.
        Remove(key);
        if (seconds < 0) seconds = kind == ToastKind.Success ? 8 : 10;
        var item = new ToastItem(key, kind, title, detail, action, run, t => post(() => Items.Remove(t)), seconds);
        Items.Add(item);
        while (Items.Count > Limit) Items.RemoveAt(0);
        if (seconds == 0) return;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), lifetime.Token)
            .ContinueWith(_ => post(() => Items.Remove(item)), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public void Remove(string key) => RemoveWhere(x => x == key);

    public void RemoveWhere(Func<string, bool> match)
    {
        foreach (var old in Items.Where(x => match(x.Key)).ToList()) Items.Remove(old);
    }

    public bool Has(Func<string, bool> match) => Items.Any(x => match(x.Key));

    /// <summary>The first lines of a long message, so a toast stays short; the full text is in Logs.</summary>
    public static string Brief(string text, int max = 240)
    {
        var flat = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
