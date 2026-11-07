namespace TidyWindow.Next.Desktop;

/// <summary>
/// Turns the raw, bursty byte counts of a download into numbers that move smoothly. The network reports hundreds of times a second and
/// in uneven steps; showing those directly makes the text flicker and the bar jump. The meter keeps the latest real count as a target,
/// eases what is shown toward it on every tick, and smooths the speed so the time left does not swing.
/// </summary>
public sealed class DownloadMeter
{
    /// <summary>How quickly the shown count catches up with the real one, in seconds.</summary>
    private const double CatchUp = 0.25;
    /// <summary>How long a speed reading takes to settle, in seconds.</summary>
    private const double SpeedSettle = 2.0;
    /// <summary>The time left is not offered until the speed has had this long to settle, in seconds.</summary>
    private const double Warmup = 1.5;

    // Report comes from the download's thread, everything else from the UI thread.
    private readonly object gate = new();
    private long received;
    private long measured;
    private long? total;
    private double shown;
    private double speed;
    private double elapsed;

    public long? Total => total;
    public double ShownBytes => shown;
    public double BytesPerSecond => speed;
    public bool Indeterminate => total is not > 0;

    /// <summary>How much of the download to draw, from 0 to 1. Never moves backwards.</summary>
    public double Fraction => total is > 0 ? Math.Clamp(shown / total.Value, 0, 1) : 0;

    /// <summary>Starts again for a new download.</summary>
    public void Reset(long? expectedTotal)
    {
        lock (gate)
        {
            received = measured = 0;
            total = expectedTotal is > 0 ? expectedTotal : null;
            shown = speed = elapsed = 0;
        }
    }

    /// <summary>Records what the network says; cheap enough to call on every read.</summary>
    public void Report(long bytesReceived, long? totalBytes)
    {
        lock (gate)
        {
            if (bytesReceived > received) received = bytesReceived;
            if (totalBytes is > 0) total = totalBytes;
        }
    }

    /// <summary>Moves the shown numbers on by <paramref name="seconds"/>. Call this at a steady rate from a UI timer.</summary>
    public void Tick(double seconds)
    {
        if (seconds <= 0) return;
        lock (gate)
        {
            elapsed += seconds;
            var instant = (received - measured) / seconds;
            measured = received;
            speed += (instant - speed) * Blend(seconds, SpeedSettle);
            shown += (received - shown) * Blend(seconds, CatchUp);
            if (received - shown < 1024) shown = received; // under a kilobyte is invisible at one decimal, and easing never quite arrives
        }
    }

    /// <summary>The share of the way a value covers toward its goal after <paramref name="seconds"/>, for a given settling time.</summary>
    private static double Blend(double seconds, double settle) => 1 - Math.Exp(-seconds / settle);

    /// <summary>"12.4 of 45.6 MB", both in the unit the whole download is measured in, with one decimal so the text keeps its width.</summary>
    public string Progress()
    {
        var unit = Unit(total ?? (long)shown);
        return total is > 0 ? $"{Scaled(shown, unit)} of {Scaled(total.Value, unit)} {unit.Name}" : $"{Scaled(shown, unit)} {unit.Name} downloaded";
    }

    /// <summary>"3.2 MB/s · 14 s left", or nothing until there is a steady speed to report.</summary>
    public string Pace()
    {
        if (elapsed < Warmup || speed < 1) return "";
        var unit = Unit((long)speed);
        var rate = $"{Scaled(speed, unit)} {unit.Name}/s";
        if (total is not > 0) return rate;
        var left = Math.Max(0, total.Value - received) / speed;
        return $"{rate} · {Remaining(left)}";
    }

    public string Percent() => total is > 0 ? $"{(int)(Fraction * 100)}%" : "";

    private static string Remaining(double seconds) => seconds switch
    {
        < 1.5 => "almost done",
        < 60 => $"{Math.Round(seconds)} s left",
        < 3600 => $"{(int)(seconds / 60)} min {(int)(seconds % 60):00} s left",
        _ => $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60):00} min left"
    };

    private static (string Name, double Size) Unit(long bytes) => bytes switch
    {
        < 1024 => ("B", 1),
        < 1024 * 1024 => ("KB", 1024),
        _ => ("MB", 1024d * 1024d)
    };

    private static string Scaled(double bytes, (string Name, double Size) unit) => (bytes / unit.Size).ToString(unit.Size == 1 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture);
}
