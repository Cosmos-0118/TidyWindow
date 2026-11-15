using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>One battery in the details view: the answer first, then the facts behind it.</summary>
public sealed class BatteryDetailRow(BatteryReading reading, int number, int total) : ObservableState
{
    public BatteryReading Reading { get; } = reading;

    public string Title => total > 1 ? $"Battery {number} of {total}" : "Battery";
    public Visibility TitleVisibility => total > 1 ? Visibility.Visible : Visibility.Collapsed;
    public string Headline => Reading.HealthPercent is { } percent ? $"{HealthViewModel.Whole(percent)}% · {HealthViewModel.GradeWord(Reading.Grade)}" : "Health unknown";
    public string Subline => Reading.Grade switch
    {
        BatteryGrade.Good => "This battery still holds most of the charge it had when it was new.",
        BatteryGrade.Fair => "This battery holds noticeably less than when it was new, so a full charge lasts a shorter time.",
        BatteryGrade.Worn => "This battery holds much less than when it was new. If its life between charges is a problem, a replacement will help.",
        _ => "This battery does not say how much it held when it was new, so its health cannot be worked out."
    };
    public Visibility GoodVisibility => Reading.Grade == BatteryGrade.Good ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarnVisibility => Reading.Grade is BatteryGrade.Fair or BatteryGrade.Worn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeutralVisibility => Reading.Grade == BatteryGrade.Unknown ? Visibility.Visible : Visibility.Collapsed;
    public double Percent => Reading.HealthPercent ?? 0;
    public Visibility BarVisibility => Reading.HealthPercent is null ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<StartupFact> Facts { get; } = BuildFacts(reading);

    private static List<StartupFact> BuildFacts(BatteryReading r)
    {
        string Capacity(long value) => value <= 0 ? "Not reported" : r.Relative ? $"{value:N0} (the battery's own units)" : $"{value:N0} mWh";
        var facts = new List<StartupFact>
        {
            new("Full charge", Capacity(r.FullChargeCapacity), false),
            new("Design capacity", Capacity(r.DesignCapacity), false)
        };
        if (r is { HealthPercent: not null, WornCapacity: { } worn, WearPercent: { } wear })
            facts.Add(new("Lost to wear", (r.Relative ? $"{worn:N0}" : $"{worn:N0} mWh") + $" · {wear:0.#}% of the original", false));
        facts.Add(new("Cycle count", r.CycleCount is { } cycles ? $"{cycles:N0}" : "Not reported", false));
        if (r.Chemistry.Length > 0) facts.Add(new("Chemistry", r.Chemistry, false));
        if (r.Manufacturer.Length > 0) facts.Add(new("Manufacturer", r.Manufacturer, false));
        if (r.Name.Length > 0) facts.Add(new("Name", r.Name, false));
        return facts;
    }
}

/// <summary>The Battery card on the Health page: what it says, and whether it can be checked now.</summary>
public sealed class BatteryDeviceRow : ObservableState
{
    private readonly bool present;
    private bool busy, failed;
    private IReadOnlyList<BatteryReading>? readings;
    private DateTime? checkedAt;
    private string failure = "";

    public BatteryDeviceRow(bool present) => this.present = present;

    public string Title => "Battery";
    public string Glyph => "\uE83F";
    public bool HasReading => readings is { Count: > 0 };

    private BatteryGrade Grade => BatteryReading.GradeFor(Combined);
    private double? Combined => readings is { Count: > 0 } ? BatteryReading.CombinedPercent(readings) : null;

    /// <summary>The word beside the title. State is always said in words, never by colour alone.</summary>
    public string StatusWord => !present ? "None found" : busy && !HasReading ? "Checking" : failed ? "Could not read" : !HasReading ? (checkedAt is null ? "Not checked" : "None found") : Combined is null ? "Unknown" : HealthViewModel.GradeWord(Grade);
    public Visibility GoodVisibility => HasReading && Grade == BatteryGrade.Good ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarnVisibility => failed || HasReading && Grade is BatteryGrade.Fair or BatteryGrade.Worn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MutedVisibility => GoodVisibility == Visibility.Collapsed && WarnVisibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;

    public string Summary
    {
        get
        {
            if (!present) return "No battery in this PC.";
            if (failed) return failure;
            if (!HasReading) return checkedAt is null ? "Find out how much charge the battery can still hold compared with when it was new." : "Windows did not report a battery on this PC.";
            var when = checkedAt is { } at ? $" · checked {at:h:mm tt}" : "";
            if (Combined is null) return "This battery does not say how much it held when it was new, so its health cannot be worked out" + when;
            var usable = readings!.Where(x => x.HealthPercent is not null && !x.Relative).ToList();
            if (usable.Count == 0) return $"Holds {HealthViewModel.Whole(Combined.Value)}% of the charge it held when new{when}";
            var count = usable.Count > 1 ? $" across {usable.Count} batteries" : "";
            return $"Holds {usable.Sum(x => x.FullChargeCapacity):N0} of {usable.Sum(x => x.DesignCapacity):N0} mWh{count}{when}";
        }
    }

    public string PercentText => Combined is { } percent ? HealthViewModel.Whole(percent).ToString() : "";
    public double Percent => Combined ?? 0;
    public Visibility ResultVisibility => HasReading && Combined is not null && !busy ? Visibility.Visible : Visibility.Collapsed;

    public bool CanCheck => present && !busy;
    public Visibility ButtonVisibility => present ? Visibility.Visible : Visibility.Collapsed;
    public string ButtonText => busy ? "Checking…" : failed ? "Try again" : checkedAt is not null && HasReading ? "Check again" : "Get battery health";
    public Visibility BusyVisibility => busy ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>For the details view, where the card's summary line is not on screen: the reason the last check failed.</summary>
    public Visibility FailedVisibility => failed && !busy ? Visibility.Visible : Visibility.Collapsed;
    public string FailureText => failure;
    public Visibility DetailsLinkVisibility => HasReading && !busy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FooterVisibility => DetailsLinkVisibility;
    public string AutomationName => HasReading && Combined is { } p ? $"Battery health {HealthViewModel.Whole(p)} percent, {StatusWord}" : "Battery health";

    public void Begin() { busy = true; Refresh(); }

    public void Succeed(IReadOnlyList<BatteryReading> result, DateTime at)
    {
        busy = false; failed = false; failure = ""; readings = result; checkedAt = at; Refresh();
    }

    public void Fail(string message, DateTime at)
    {
        busy = false; failed = true; failure = message; checkedAt = at; Refresh();
    }

    private void Refresh()
    {
        foreach (var name in new[]
        {
            nameof(HasReading), nameof(StatusWord), nameof(GoodVisibility), nameof(WarnVisibility), nameof(MutedVisibility), nameof(Summary), nameof(PercentText), nameof(Percent),
            nameof(ResultVisibility), nameof(CanCheck), nameof(ButtonText), nameof(BusyVisibility), nameof(FailedVisibility), nameof(FailureText), nameof(DetailsLinkVisibility), nameof(FooterVisibility), nameof(AutomationName)
        }) Changed(name);
    }
}

public sealed class HealthViewModel : ObservableState, IDisposable
{
    private readonly IBatteryReader reader;
    private readonly ActivityLog log;
    private readonly Func<DateTime> now;
    private bool showingDetails, disposed;

    public HealthViewModel(IBatteryReader reader, bool hasBattery, ActivityLog log, Func<DateTime>? now = null)
    {
        this.reader = reader; this.log = log; this.now = now ?? (() => DateTime.Now);
        Battery = new BatteryDeviceRow(hasBattery);
        Devices = [Battery];
    }

    /// <summary>The cards on the page. Battery is the only device for now; another device is one more row here.</summary>
    public IReadOnlyList<BatteryDeviceRow> Devices { get; }
    public BatteryDeviceRow Battery { get; }
    public BulkObservableCollection<BatteryDetailRow> Batteries { get; } = new();

    public static int Whole(double percent) => (int)Math.Round(percent, MidpointRounding.AwayFromZero);

    public static string GradeWord(BatteryGrade grade) => grade switch { BatteryGrade.Good => "Good", BatteryGrade.Fair => "Fair", BatteryGrade.Worn => "Worn", _ => "Unknown" };

    // ---- Which part of the page is showing ----

    public bool ShowingDetails { get => showingDetails; private set { if (Set(ref showingDetails, value)) { Changed(nameof(ListVisibility)); Changed(nameof(DetailsVisibility)); } } }
    public Visibility ListVisibility => showingDetails ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailsVisibility => showingDetails ? Visibility.Visible : Visibility.Collapsed;

    public void OpenDetails() { if (Battery.HasReading) ShowingDetails = true; }

    /// <summary>Back to the list of devices. Also what opening Health from the sidebar does.</summary>
    public void ShowDevices() => ShowingDetails = false;

    // ---- Reading ----

    /// <summary>The check behind both the card's button and the details view's. A second request while one runs is ignored.</summary>
    public async Task CheckBatteryAsync()
    {
        if (!Battery.CanCheck) return;
        Battery.Begin();
        try
        {
            var result = await Task.Run(reader.Read);
            if (disposed) return;
            Batteries.Reset(result.Select((x, i) => new BatteryDetailRow(x, i + 1, result.Count)).ToList());
            Battery.Succeed(result, now());
            // A reading that leaves the person on the details page with nothing to show would be a dead end.
            if (result.Count == 0) ShowDevices();
        }
        // Anything at all: this runs from a click, so an unexpected failure must show on the card and in Logs instead of ending the process.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (disposed) return;
            Battery.Fail(ex is InvalidOperationException ? ex.Message : "The battery could not be read. " + ex.Message, now());
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Battery health could not be read", ex.ToString());
        }
    }

    public void Dispose() => disposed = true;
}
