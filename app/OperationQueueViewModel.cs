using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class QueueRow(QueueEntry entry) : ObservableState
{
    public QueueEntry Entry { get; private set; } = entry;
    private bool expanded;
    private bool light;
    private IReadOnlyList<FormattedOutputLine>? formatted;
    public void SetLightTheme(bool value) { light = value; Changed(nameof(StatusBrush)); Changed(nameof(NoticeBrush)); }
    private OutputTone StatusTone => Entry.State == QueueItemState.Running ? OutputTone.Progress : Entry.Result?.Outcome switch { ActionOutcome.Completed => OutputTone.Success, ActionOutcome.Failed => OutputTone.Error, ActionOutcome.RestartRequired or ActionOutcome.Unverified => OutputTone.Warning, _ => OutputTone.Normal };
    public Microsoft.UI.Xaml.Media.Brush StatusBrush => OutputPalette.Brush(StatusTone, light);
    public Microsoft.UI.Xaml.Media.Brush NoticeBrush => OutputPalette.Brush(Entry.State == QueueItemState.Running ? OutputTone.Information : StatusTone, light);
    public IReadOnlyList<FormattedOutputLine> OutputDocument => formatted ??= CommandOutputFormatter.Format(Entry.OutputSegments is { Count: > 0 } segments ? segments : [new CommandOutput(Output)]);
    public string Name => Entry.Operation.Name;
    public string Detail => Entry.Operation.Fix is not null ? $"Fix · {Entry.Operation.Id}"
        : $"{Entry.Operation.Kind} · {ManagerDefinition.All.Single(x => x.Kind == Entry.Operation.Manager).Name} · {Entry.Operation.Id}" + (Entry.Operation.Interactive ? " · Installer options" : "");
    public string Status => Entry.State switch
    {
        QueueItemState.Waiting => "Queued",
        QueueItemState.Running => "Running",
        QueueItemState.Cancelled => "Removed",
        _ => Entry.Result?.Outcome switch { ActionOutcome.Completed => "Completed", ActionOutcome.Failed => "Failed", ActionOutcome.RestartRequired => "Restart required", _ => "Check result" }
    };
    public string Notice => Entry.State == QueueItemState.Running ? Entry.Notice : Entry.Result?.Message ?? "";
    public Visibility NoticeVisibility => Notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string OutputTitle => Entry.State == QueueItemState.Finished && Entry.Output.Length == 0 ? "Result details" : "Command output";
    public string Output
    {
        get
        {
            var text = Entry.Output.Length > 0 ? Entry.Output : Entry.State == QueueItemState.Finished ? Entry.Result?.Detail ?? "" : Entry.State == QueueItemState.Waiting ? "Output will appear when this operation starts." : "No captured output yet. Some commands buffer their output or write to a separate window.";
            return string.Concat(PackageOutputParser.Clean(text).Replace("\r\n", "\n").Replace('\r', '\n').Where(c => !char.IsControl(c) || c is '\n' or '\t'));
        }
    }
    public Visibility OutputVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "\uE70E" : "\uE70D";
    public string ExpandName => $"{Name}, {Status}. {(expanded ? "Hide" : "Show")} command output";
    public Visibility CancelVisibility => Entry.State == QueueItemState.Waiting ? Visibility.Visible : Visibility.Collapsed;
    public string CancelName => $"Remove {Name} from queue";
    public double OutputHeight { get; private set; } = 240;
    public void SetOutputHeight(double value) { if (OutputHeight == value) return; OutputHeight = value; Changed(nameof(OutputHeight)); }
    public void Toggle() { expanded = !expanded; Changed(nameof(OutputVisibility)); Changed(nameof(Chevron)); Changed(nameof(ExpandName)); if (expanded) Changed(nameof(OutputDocument)); }
    public void Update(QueueEntry value)
    {
        var old = Entry; Entry = value;
        if (old.Output != value.Output || old.OutputSegments != value.OutputSegments || old.State != value.State || old.Result != value.Result)
        { formatted = null; Changed(nameof(Output)); if (expanded) Changed(nameof(OutputDocument)); }
        if (old.Notice != value.Notice || old.State != value.State || old.Result != value.Result) { Changed(nameof(Notice)); Changed(nameof(NoticeVisibility)); }
        if (old.State != value.State || old.Operation != value.Operation || old.Result != value.Result)
            foreach (var name in new[] { nameof(Name), nameof(Detail), nameof(Status), nameof(StatusBrush), nameof(NoticeBrush), nameof(OutputTitle), nameof(ExpandName), nameof(CancelVisibility) }) Changed(name);
    }
    public override string ToString() => $"{Name}. {Detail}. {Status}";
}

public sealed class OperationQueueViewModel : ObservableState, IDisposable
{
    private readonly PackageOperationQueue queue;
    private readonly ActivityLog log;
    private readonly Action<Action> post;
    private readonly Timer updateTimer;
    private readonly Dictionary<Guid, QueueRow> byId = [];
    private int updatePending;
    private volatile bool disposed;
    private double outputHeight = 240;
    private bool showInstallerOptions;
    private bool light;
    public void SetLightTheme(bool value) { if (light == value) return; light = value; foreach (var row in Rows) row.SetLightTheme(value); }
    public bool ShowInstallerOptions { get => showInstallerOptions; set => Set(ref showInstallerOptions, value); }
    public QueueSnapshot Snapshot { get; private set; } = new(-1, []);
    public ObservableCollection<QueueRow> Rows { get; } = [];
    public bool HasWork => Snapshot.HasWork;
    public string Label => HasWork ? $"Queue ({Snapshot.Entries.Count(x => x.State is QueueItemState.Waiting or QueueItemState.Running)})" : "Queue";
    public bool CanClear => Snapshot.Entries.Any(x => x.State == QueueItemState.Waiting);
    public bool CanClearFinished => Snapshot.Entries.Any(x => x.State is QueueItemState.Finished or QueueItemState.Cancelled);
    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public event Action? StateChanged;
    /// <summary>Raised on the UI thread after every update, including new output, for pages that show one line of live progress.</summary>
    public event Action? Refreshed;
    public OperationQueueViewModel(PackageOperationQueue queue, ActivityLog log, Action<Action> post)
    {
        this.queue = queue; this.log = log; this.post = post;
        OutputPalette.Changed += RefreshColors;
        updateTimer = new(_ => { if (!disposed) post(Apply); }, null, Timeout.Infinite, Timeout.Infinite);
        queue.Changed += QueueChanged; queue.Finished += QueueFinished;
        Apply();
    }
    private void RefreshColors() { foreach (var row in Rows) row.SetLightTheme(light); }
    public void Enqueue(IEnumerable<PackageOperation> operations)
    {
        queue.Enqueue(operations.Select(x => x with { Interactive = x.Interactive || ShowInstallerOptions && x.Fix is null && x.Manager != ManagerKind.Scoop && x.Kind != PackageOperationKind.Uninstall }));
        Apply();
    }
    public void Cancel(QueueRow row) { queue.Cancel(row.Entry.Id); Apply(); }
    public void CancelWaiting() { queue.CancelWaiting(); Apply(); }
    public void ClearFinished() { queue.ClearFinished(); Apply(); }
    public void SetOutputHeight(double value) { outputHeight = value; foreach (var row in Rows) row.SetOutputHeight(value); }
    private void QueueChanged()
    {
        // Coalesce output bursts into at most ten UI updates per second. Keep stable rows and expansion state.
        if (!disposed && Interlocked.Exchange(ref updatePending, 1) == 0)
            try { updateTimer.Change(100, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }
    private void QueueFinished(QueueEntry entry)
    {
        var result = entry.Result!;
        log.Write(result.Outcome == ActionOutcome.Completed ? ActivityLevel.Success : result.Outcome == ActionOutcome.Failed ? ActivityLevel.Error : ActivityLevel.Warning,
            entry.Operation.Fix is not null ? ActivityArea.App : ActivityArea.Install, result.Message, result.Detail);
    }
    private void Apply()
    {
        Interlocked.Exchange(ref updatePending, 0);
        if (disposed) return;
        var snapshot = queue.Snapshot();
        if (snapshot.Revision <= Snapshot.Revision) return;
        var stateChanged = snapshot.StateVersion != Snapshot.StateVersion;
        Snapshot = snapshot;
        var ordered = snapshot.Entries.Where(x => x.State is QueueItemState.Running or QueueItemState.Waiting)
            .Concat(snapshot.Entries.Reverse().Where(x => x.State is QueueItemState.Finished or QueueItemState.Cancelled)).ToArray();
        var ids = ordered.Select(x => x.Id).ToHashSet();
        foreach (var removed in byId.Keys.Where(x => !ids.Contains(x)).ToArray()) { Rows.Remove(byId[removed]); byId.Remove(removed); }
        for (var i = 0; i < ordered.Length; i++)
        {
            var entry = ordered[i];
            if (!byId.TryGetValue(entry.Id, out var row))
            { row = new(entry); row.SetOutputHeight(outputHeight); row.SetLightTheme(light); byId[entry.Id] = row; Rows.Insert(i, row); }
            else { row.Update(entry); var index = Rows.IndexOf(row); if (index != i) Rows.Move(index, i); }
        }
        if (stateChanged)
        {
            foreach (var name in new[] { nameof(HasWork), nameof(Label), nameof(CanClear), nameof(CanClearFinished), nameof(EmptyVisibility) }) Changed(name);
            StateChanged?.Invoke();
        }
        Refreshed?.Invoke();
    }
    /// <summary>The newest queue entry for a fix, if any.</summary>
    public QueueEntry? FindFix(string id) => Snapshot.Find(PackageOperation.FixResource(id));
    /// <summary>Latest entry per package, so a long list can be matched without rescanning the queue for every row.</summary>
    public Dictionary<string, QueueEntry> Index()
    {
        var index = new Dictionary<string, QueueEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Snapshot.Entries) index[entry.Operation.ResourceKey] = entry;
        return index;
    }
    public QueueEntry? Find(ManagerKind manager, string id, bool global = false) => Snapshot.Find(PackageOperation.Resource(manager, id, global));
    public bool Contains(ManagerKind manager, string id, bool global = false) => Find(manager, id, global)?.State is QueueItemState.Waiting or QueueItemState.Running;
    public void Dispose() { disposed = true; updateTimer.Dispose(); OutputPalette.Changed -= RefreshColors; queue.Changed -= QueueChanged; queue.Finished -= QueueFinished; }
}
