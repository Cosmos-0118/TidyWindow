namespace TidyWindow.Next.Desktop;

public enum ToastKind { Info, Success, Warning }

/// <summary>The rules for how long a toast stays and how much text it will carry. Kept apart from the window so they can be tested.</summary>
public static class ToastPolicy
{
    public const int MaxTitle = 160;
    public const int MaxDetail = 4000;
    private const double MaxSeconds = 30;

    /// <summary>
    /// How long a toast stays. A request of 0 (until dismissed) or more is kept as it is. Otherwise it is long enough to read: the usual time for its kind,
    /// or more when there is a lot of text, so a long error is not gone before the person has read it.
    /// </summary>
    public static double Seconds(ToastKind kind, string title, string detail, double requested = -1)
    {
        if (requested >= 0) return requested;
        var usual = kind == ToastKind.Success ? 8 : 10;
        var reading = 3 + (title.Length + detail.Length) / 14.0;
        return Math.Min(MaxSeconds, Math.Max(usual, Math.Round(reading)));
    }

    /// <summary>Keeps absurdly long text from reaching the screen. The full text belongs in Logs.</summary>
    public static string Clamp(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>The first part of a long message on one line, so a toast stays short.</summary>
    public static string Brief(string text, int max = 240)
    {
        var flat = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }
}

/// <summary>
/// The time a toast has left. It stops while anything holds it (the pointer is over it, it is open for reading, the window is not in use) and carries on
/// from where it was, so a toast never disappears while someone is looking at it.
/// </summary>
public sealed class Countdown
{
    private readonly Func<long> now;
    private readonly HashSet<string> holds = [];
    private long left;
    private long since;

    /// <param name="now">A clock in milliseconds that only goes forward.</param>
    public Countdown(TimeSpan total, Func<long> now)
    {
        this.now = now;
        Total = total;
        Restart();
    }

    public TimeSpan Total { get; private set; }
    public bool IsHeld => holds.Count > 0;
    /// <summary>Zero seconds means the toast stays until it is dismissed.</summary>
    public bool Endless => Total <= TimeSpan.Zero;
    public TimeSpan Remaining => TimeSpan.FromMilliseconds(Math.Max(0, IsHeld ? left : left - (now() - since)));
    public bool Expired => !Endless && Remaining <= TimeSpan.Zero;
    /// <summary>How much of the time is left, from 1 down to 0.</summary>
    public double Fraction => Endless ? 1 : Math.Clamp(Remaining / Total, 0, 1);

    public void Restart(TimeSpan? total = null)
    {
        if (total is { } value) Total = value;
        left = (long)Total.TotalMilliseconds; since = now();
    }

    /// <returns>True when the countdown went from running to held or back.</returns>
    public bool Hold(string reason, bool on)
    {
        var wasHeld = IsHeld;
        if (on == holds.Contains(reason)) return false;
        // Settle what has run so far before the state changes, so held time is not counted.
        if (!wasHeld) left = Math.Max(0, left - (now() - since));
        if (on) holds.Add(reason); else holds.Remove(reason);
        if (wasHeld && !IsHeld) since = now();
        return wasHeld != IsHeld;
    }
}
