using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class FolderResult(FolderCandidate candidate) : ObservableState
{
    public FolderCandidate Candidate { get; } = candidate;
    public string Folder => Candidate.Folder;
    public string ProgramsText => Candidate.Programs.Count == 0
        ? "No programs directly inside this folder"
        : string.Join(", ", Candidate.Programs.Take(4)) + (Candidate.Programs.Count > 4 ? $" and {Candidate.Programs.Count - 4} more" : "");
}

/// <summary>The "Add a folder" window: suggests folders that hold programs, searches them as you type, and takes a pasted path.</summary>
public sealed class AddFolderViewModel(PathFinder finder, PathSnapshot snapshot) : ObservableState, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> inPath = snapshot.Search.Select(x => PathAnalyzer.Key(x.Folder)).ToHashSet();
    private IReadOnlyList<FolderCandidate> all = [];
    private bool scanning = true;
    private string query = "", heading = "", hint = "", empty = "";
    private FolderResult? selected;

    public ObservableCollection<FolderResult> Results { get; } = [];
    public bool Scanning => scanning;
    public Visibility ScanVisibility => scanning ? Visibility.Visible : Visibility.Collapsed;
    public string Heading { get => heading; private set => Set(ref heading, value); }
    public string Hint { get => hint; private set { Set(ref hint, value); Changed(nameof(HintVisibility)); } }
    public Visibility HintVisibility => hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyText { get => empty; private set { Set(ref empty, value); Changed(nameof(EmptyVisibility)); } }
    public Visibility EmptyVisibility => empty.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CanAdd => selected is not null;
    public FolderResult? Selected { get => selected; set { if (Set(ref selected, value)) Changed(nameof(CanAdd)); } }

    public string Query
    {
        get => query;
        set { if (Set(ref query, value)) Refilter(); }
    }

    public async Task ScanAsync()
    {
        try { all = await Task.Run(() => finder.Scan(snapshot, lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { all = []; }
        scanning = false;
        Changed(nameof(Scanning)); Changed(nameof(ScanVisibility));
        Refilter();
    }

    private static bool LooksLikePath(string text) => text.Contains('\\') || text.Contains('/') || text.StartsWith('%') || (text.Length >= 2 && text[1] == ':');

    private void Refilter()
    {
        var text = query.Trim().Trim('"');
        var keep = selected?.Folder;
        IReadOnlyList<FolderCandidate> list = [];
        var typedPath = false;
        var none = "";
        if (text.Length == 0)
        {
            list = PathFinder.Suggest(all);
            Heading = "Suggested folders";
            Hint = "Folders that hold command-line programs and are not in your PATH yet.";
            if (!scanning && list.Count == 0) none = "No suggestions found. Search for a program by name, or browse for the folder yourself.";
        }
        else if (LooksLikePath(text))
        {
            typedPath = true;
            Heading = "That folder";
            Hint = "";
            var folder = Environment.ExpandEnvironmentVariables(text);
            if (!Directory.Exists(folder)) none = "There is no folder at that path.";
            else if (inPath.Contains(PathAnalyzer.Key(folder))) none = "That folder is already in your PATH.";
            else list = [finder.Describe(folder)];
        }
        else
        {
            list = PathFinder.Search(all, text);
            Heading = $"Matches for “{text}”";
            Hint = "";
            if (!scanning && list.Count == 0) none = $"Nothing found for “{text}”. Folders already in your PATH are left out. Try the program's name, or browse for the folder yourself.";
        }
        EmptyText = none;

        Results.Clear();
        foreach (var candidate in list) Results.Add(new FolderResult(candidate));
        Selected = typedPath ? Results.FirstOrDefault() : Results.FirstOrDefault(x => string.Equals(x.Folder, keep, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
