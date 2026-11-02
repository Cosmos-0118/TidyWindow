using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed record ScoutRow(ScoutEntry Entry)
{
    public string Name => Entry.Name;
    public string Path => Entry.FullPath;
    public string Detail => Entry.IsFolder ? "Folder" : Entry.Size is { } size ? size >= 1073741824 ? $"{size / 1073741824d:0.##} GB" : size >= 1048576 ? $"{size / 1048576d:0.##} MB" : size >= 1024 ? $"{size / 1024d:0.#} KB" : $"{size} B" : "File";
    public string Glyph => Entry.IsFolder ? "\uE8B7" : "\uE8A5";
    public string OpenLabel => $"Show {Name} in File Explorer";
    public string CopyLabel => $"Copy the path to {Name}";
    public override string ToString() => $"{Name}. {Detail}. {Path}";
}

public sealed class ScoutViewModel : ObservableState, IDisposable
{
    private readonly ScoutService service;
    private readonly Action<Action> post;
    private readonly ActivityLog log;
    private CancellationTokenSource? searchCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private long generation;
    private bool indexed;
    private bool searching;
    private bool checkingBackend;
    private bool usingFallback;
    private bool installing;
    private ScoutBackendStatus backend = new(false, false);
    private string query = "";
    private string? root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private ScoutItemKind kind;
    public BulkObservableCollection<ScoutRow> Rows { get; } = [];
    public bool Indexed { get => indexed; private set { Set(ref indexed, value); Changed(nameof(SetupVisibility)); } }
    public Visibility SetupVisibility => Indexed ? Visibility.Collapsed : Visibility.Visible;
    public bool NeedsInstallation => !backend.Installed;
    public bool NeedsBackendCheck => backend.Installed && !Indexed;
    public bool CanSetup => !installing && !checkingBackend && !backend.Indexing;
    public string SetupLabel => installing ? "Installing…" : checkingBackend ? "Checking…" : NeedsInstallation ? "Enable instant search" : backend.Indexing ? "Indexing…" : "Retry instant search";
    private string BackendInstruction => NeedsInstallation ? "Enable instant search, or choose a folder to search without it." : backend.Error ?? "Everything is installed; its index is not ready yet. Retry instant search, or choose a folder.";
    public bool Searching { get => searching; private set { Set(ref searching, value); Changed(nameof(ProgressVisibility)); Changed(nameof(EmptyVisibility)); } }
    public Visibility ProgressVisibility => Searching ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Rows.Count == 0 && !Searching ? Visibility.Visible : Visibility.Collapsed;
    private string status = "Search file and folder names";
    public string Status { get => status; private set => Set(ref status, value); }
    public string EmptyTitle => query.Length == 0 ? (backend.Indexing ? "Preparing instant search" : "Find files and folders") : Error.Length > 0 ? "Search needs attention" : "No matching items";
    public string EmptyDetail => query.Length == 0 ? (Indexed ? "Search by name or path. Choose a folder or use the whole-computer index." : "Search by name or path. Until the whole-computer index is ready, Scout searches your user folder; choose another folder any time.")
        : Error.Length > 0 ? "Open Logs for details, or choose a folder to search directly." : Notice.Length > 0 ? Notice : "Try another name, location or file type.";
    private string notice = "";
    /// <summary>Worth knowing, not a failure: instant search is off so the user folder is searched instead, the search text needs a change, or the folder index may be out of date. Never raised as a problem.</summary>
    public string Notice { get => notice; private set { if (Set(ref notice, value)) { Changed(nameof(NoticeVisibility)); Changed(nameof(EmptyDetail)); } } }
    public Visibility NoticeVisibility => Notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private string error = "";
    /// <summary>A real failure only: the search threw, the index worker failed, or a location could not be opened. The shell turns each new value into a warning toast, so ordinary states never go here.</summary>
    public string Error { get => error; private set { if (Set(ref error, value)) { Changed(nameof(ErrorVisibility)); Changed(nameof(EmptyTitle)); Changed(nameof(EmptyDetail)); } } }
    public Visibility ErrorVisibility => Error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public ScoutViewModel(ScoutService service, ActivityLog log, Action<Action> post) { this.service = service; this.log = log; this.post = post; }
    public async Task CheckBackendAsync()
    {
        if (checkingBackend || lifetime.IsCancellationRequested) return;
        checkingBackend = true; NotifyBackend();
        try
        {
            backend = await service.CheckBackendAsync(lifetime.Token);
            Indexed = backend.Ready;
            // Without the index Scout still works: whole-computer searches fall back to the user folder. The reason stays visible, as a notice, not a problem.
            Notice = Indexed ? "" : BackendInstruction; Error = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = "Instant search could not be checked. " + ex.Message; }
        finally { checkingBackend = false; NotifyBackend(); }
    }
    private void NotifyBackend()
    {
        foreach (var name in new[] { nameof(SetupVisibility), nameof(SetupLabel), nameof(CanSetup), nameof(NeedsInstallation), nameof(NeedsBackendCheck), nameof(EmptyDetail), nameof(EmptyTitle) }) Changed(name);
    }
    public void SetInstallationPending(bool value) { if (installing == value) return; installing = value; NotifyBackend(); }
    public async Task SearchAsync(string text, string? folder, ScoutItemKind type, bool debounce = true, bool directFolder = false)
    {
        var current = ++generation;
        searchCancellation?.Cancel(); searchCancellation?.Dispose(); searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = searchCancellation.Token;
        query = text.Trim(); root = folder; kind = type;
        Error = ""; Notice = ""; Rows.Reset([]); Changed(nameof(EmptyTitle)); Changed(nameof(EmptyDetail));
        if (query.Length == 0) { Searching = false; Status = "Search file and folder names"; return; }
        // A whole-computer search with no index falls back to the user folder instead of failing.
        var fallback = !Indexed && root is null;
        if (fallback) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        usingFallback = fallback;
        var useIndex = Indexed && !directFolder;
        // Text the search cannot use (too many words, a wildcard carrying index commands) is pointed out here, not sent on to come back as a failure.
        try { _ = new ScoutQuery(query); }
        catch (ArgumentException ex) { Searching = false; Notice = ex.Message; Status = "Check the search text"; Changed(nameof(EmptyVisibility)); return; }
        Searching = true; Status = useIndex ? "Searching the local index…" : fallback ? "Searching your user folder…" : "Searching folder index…";
        if (fallback) Notice = BackendInstruction;
        try
        {
            if (debounce) await Task.Delay(140, token);
            var progress = new SearchProgress(value => post(() => { if (current == generation && !token.IsCancellationRequested) Apply(value); }));
            var request = new ScoutRequest(query, root, kind);
            var result = await Task.Run(() => service.SearchAsync(request, useIndex, progress, token), token);
            if (current == generation && !token.IsCancellationRequested) Apply(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (current == generation) { Error = ex.Message; Status = "Search could not finish"; log.Write(ActivityLevel.Warning, ActivityArea.App, "Scout search could not finish", ex.Message); }
        }
        finally { if (current == generation) { Searching = false; Changed(nameof(EmptyVisibility)); } }
    }
    private void Apply(ScoutResult result)
    {
        Rows.Reset(result.Entries.Select(x => new ScoutRow(x)));
        Status = $"{Rows.Count} shown · {result.Engine}" + (usingFallback ? " (your user folder; the whole-computer index is not ready)" : "") + (!result.Complete ? $" · indexing {result.Checked:N0} items…" : "")
            + (result.Skipped > 0 ? $" · {result.Skipped:N0} folders inaccessible" : "")
            + (result.Limited ? " · candidate limit reached" : "")
            + (result.Notice is { } note ? " · " + note : "");
        Error = result.Error ?? "";
        Notice = string.Join(" ", new[] { usingFallback ? BackendInstruction : "", result.Notice ?? "" }.Where(x => x.Length > 0));
        Changed(nameof(EmptyVisibility));
    }
    public void RefreshFolder() => service.RefreshFolder();
    public void ReportError(string message) { Cancel(); Error = message; Status = "Search needs attention"; }
    public void Cancel() { ++generation; searchCancellation?.Cancel(); Searching = false; Status = "Search stopped"; }
    public void Dispose() { lifetime.Cancel(); searchCancellation?.Cancel(); service.Dispose(); searchCancellation?.Dispose(); lifetime.Dispose(); }
    private sealed class SearchProgress(Action<ScoutResult> action) : IProgress<ScoutResult> { public void Report(ScoutResult value) => action(value); }
}
