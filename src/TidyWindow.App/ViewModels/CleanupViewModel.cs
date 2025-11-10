using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Cleanup;

namespace TidyWindow.App.ViewModels;

public enum CleanupExtensionFilterMode
{
    None,
    IncludeOnly,
    Exclude
}

public sealed record CleanupItemKindOption(CleanupItemKind Kind, string Label)
{
    public override string ToString() => Label;
}

public sealed record CleanupExtensionFilterOption(CleanupExtensionFilterMode Mode, string Label, string Description)
{
    public override string ToString() => Label;
}

public sealed record CleanupExtensionProfile(string Name, string Description, IReadOnlyList<string> Extensions)
{
    public override string ToString() => Name;
}

public enum CleanupPreviewSortMode
{
    Impact,
    Newest,
    Risk
}

public sealed record CleanupPreviewSortOption(CleanupPreviewSortMode Mode, string Label, string Description)
{
    public override string ToString() => Label;
}

public enum CleanupPhase
{
    Setup,
    Preview
}

public enum CleanupDeletionRiskSeverity
{
    Info,
    Caution,
    Danger
}

public sealed record CleanupDeletionRiskViewModel(string Title, string Description, CleanupDeletionRiskSeverity Severity);

public sealed partial class CleanupViewModel : ViewModelBase
{
    private readonly CleanupService _cleanupService;
    private readonly MainViewModel _mainViewModel;
    private readonly IPrivilegeService _privilegeService;

    private const int PreviewCountMinimumValue = 10;
    private const int PreviewCountMaximumValue = 500;
    private const int DefaultPreviewCount = 50;

    private readonly HashSet<string> _activeExtensions = new(StringComparer.OrdinalIgnoreCase);
    private int _previewCount = DefaultPreviewCount;
    private static readonly string[] _sensitiveRoots = BuildSensitiveRoots();
    private List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)>? _pendingDeletionItems;

    public CleanupViewModel(CleanupService cleanupService, MainViewModel mainViewModel, IPrivilegeService privilegeService)
    {
        _cleanupService = cleanupService;
        _mainViewModel = mainViewModel;
        _privilegeService = privilegeService;

        ItemKindOptions = new List<CleanupItemKindOption>
        {
            new(CleanupItemKind.Files, "Files only"),
            new(CleanupItemKind.Folders, "Folders only"),
            new(CleanupItemKind.Both, "Files and folders")
        };

        ExtensionFilterOptions = new List<CleanupExtensionFilterOption>
        {
            new(CleanupExtensionFilterMode.None, "No extension filter", "Show every item regardless of extension."),
            new(CleanupExtensionFilterMode.IncludeOnly, "Include only", "Keep the extensions listed below."),
            new(CleanupExtensionFilterMode.Exclude, "Exclude", "Hide the extensions listed below.")
        };

        ExtensionProfiles = new List<CleanupExtensionProfile>
        {
            new("Documents", "Common document formats", new[] { ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".txt", ".rtf" }),
            new("Spreadsheets", "Spreadsheet data", new[] { ".xls", ".xlsx", ".ods", ".csv" }),
            new("Images", "Photo and bitmap formats", new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff" }),
            new("Media", "Audio and video clips", new[] { ".mp3", ".wav", ".mp4", ".mov", ".mkv" }),
            new("Archives", "Compressed archives", new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }),
            new("Logs", "Plain-text logs", new[] { ".log" })
        };

        SortOptions = new List<CleanupPreviewSortOption>
        {
            new(CleanupPreviewSortMode.Impact, "Largest first", "Sort by total size so high-impact files stay on top."),
            new(CleanupPreviewSortMode.Newest, "Newest", "Show most recently modified items first."),
            new(CleanupPreviewSortMode.Risk, "Risk score", "Review items with lower confidence signals before deleting.")
        };

        SelectedExtensionProfile = ExtensionProfiles.FirstOrDefault();
        RebuildExtensionCache();
    }

    [ObservableProperty]
    private bool _includeDownloads = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _headline = "Preview and clean up system clutter";

    [ObservableProperty]
    private CleanupTargetGroupViewModel? _selectedTarget;

    [ObservableProperty]
    private CleanupItemKind _selectedItemKind = CleanupItemKind.Both;

    [ObservableProperty]
    private CleanupExtensionFilterMode _selectedExtensionFilterMode = CleanupExtensionFilterMode.None;

    [ObservableProperty]
    private CleanupExtensionProfile? _selectedExtensionProfile;

    [ObservableProperty]
    private string _customExtensionInput = string.Empty;

    [ObservableProperty]
    private bool _isDeleting;

    [ObservableProperty]
    private int _deletionProgressCurrent;

    [ObservableProperty]
    private int _deletionProgressTotal;

    [ObservableProperty]
    private string _deletionStatusMessage = "Ready to delete selected items.";

    [ObservableProperty]
    private bool _isConfirmationSheetVisible;

    [ObservableProperty]
    private int _pendingDeletionItemCount;

    [ObservableProperty]
    private double _pendingDeletionTotalSizeMegabytes;

    [ObservableProperty]
    private int _pendingDeletionCategoryCount;

    [ObservableProperty]
    private string _pendingDeletionCategoryList = string.Empty;

    [ObservableProperty]
    private bool _useRecycleBin = true;

    [ObservableProperty]
    private bool _generateCleanupReport;

    [ObservableProperty]
    private CleanupPhase _currentPhase = CleanupPhase.Setup;

    public ObservableCollection<CleanupTargetGroupViewModel> Targets { get; } = new();

    public ObservableCollection<CleanupPreviewItemViewModel> FilteredItems { get; } = new();

    public ObservableCollection<CleanupDeletionRiskViewModel> PendingDeletionRisks { get; } = new();

    // Paging state for preview
    private int _currentPage = 1;
    private int _pageSize = 100;
    private int _totalFilteredItems = 0;
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value < 1) value = 1;
            if (value > TotalPages) value = TotalPages;
            if (_currentPage != value)
            {
                _currentPage = value;
                OnPropertyChanged(nameof(CurrentPage));
                RefreshFilteredItems();
                OnPropertyChanged(nameof(PageDisplay));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
            }
        }
    }
    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value < 1) value = 1;
            if (_pageSize != value)
            {
                _pageSize = value;
                OnPropertyChanged(nameof(PageSize));
                CurrentPage = 1;
                RefreshFilteredItems();
                OnPropertyChanged(nameof(PageDisplay));
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
            }
        }
    }
    public int TotalPages => (_totalFilteredItems + PageSize - 1) / PageSize;
    public string PageDisplay => $"Page {CurrentPage} of {TotalPages}";
    public bool CanGoToPreviousPage => CurrentPage > 1;
    public bool CanGoToNextPage => CurrentPage < TotalPages;

    [ObservableProperty]
    private int _selectRangeStartPage = 1;

    [ObservableProperty]
    private int _selectRangeEndPage = 1;

    [ObservableProperty]
    private CleanupPreviewSortMode _previewSortMode = CleanupPreviewSortMode.Impact;

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages)
            CurrentPage++;
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 1)
            CurrentPage--;
    }

    partial void OnPreviewSortModeChanged(CleanupPreviewSortMode value)
    {
        RefreshFilteredItems();
    }

    public IReadOnlyList<CleanupItemKindOption> ItemKindOptions { get; }

    public IReadOnlyList<CleanupExtensionFilterOption> ExtensionFilterOptions { get; }

    public IReadOnlyList<CleanupExtensionProfile> ExtensionProfiles { get; }

    public IReadOnlyList<CleanupPreviewSortOption> SortOptions { get; }

    public event EventHandler? AdministratorRestartRequested;

    public Func<string, bool>? ConfirmElevation { get; set; }

    public bool HasResults => Targets.Count > 0;

    public bool HasFilteredResults => FilteredItems.Count > 0;

    public bool IsSetupPhase => CurrentPhase == CleanupPhase.Setup;

    public bool IsPreviewPhase => CurrentPhase == CleanupPhase.Preview;

    public bool IsExtensionSelectorEnabled => SelectedExtensionFilterMode != CleanupExtensionFilterMode.None;

    public string ExtensionStatusText
    {
        get
        {
            if (SelectedExtensionFilterMode == CleanupExtensionFilterMode.None)
            {
                return "Extension filter disabled.";
            }

            if (_activeExtensions.Count == 0)
            {
                return "No extensions configured yet.";
            }

            var verb = SelectedExtensionFilterMode == CleanupExtensionFilterMode.IncludeOnly ? "Including" : "Excluding";
            var formatted = string.Join(", ", _activeExtensions.OrderBy(static x => x));
            return $"{verb} {formatted}";
        }
    }

    public int PreviewCount
    {
        get => _previewCount;
        set
        {
            var sanitized = value;
            if (sanitized < PreviewCountMinimumValue)
            {
                sanitized = PreviewCountMinimumValue;
            }
            else if (sanitized > PreviewCountMaximumValue)
            {
                sanitized = PreviewCountMaximumValue;
            }
            SetProperty(ref _previewCount, sanitized);
        }
    }

    public string SummaryText
    {
        get
        {
            if (!HasResults)
            {
                return "Run a preview to see safe cleanup candidates.";
            }

            var totalItems = Targets.Sum(static target => target.RemainingItemCount);
            var totalSizeMb = Targets.Sum(static target => target.RemainingSizeMegabytes);
            return $"Found {totalItems:N0} files totaling {totalSizeMb:F2} MB.";
        }
    }

    public int SelectedItemCount => Targets.Sum(static target => target.SelectedCount);

    public double SelectedItemSizeMegabytes => Targets.Sum(static target => target.SelectedSizeMegabytes);

    public bool HasSelection => SelectedItemCount > 0;

    public int PreviewCountMinimum
    {
        get => PreviewCountMinimumValue;
        set
        {
            // Some slider styles attempt to push values back; ignore to keep bounds immutable.
            _ = value;
        }
    }

    public int PreviewCountMaximum
    {
        get => PreviewCountMaximumValue;
        set
        {
            _ = value;
        }
    }

    public string SelectionSummaryText => HasSelection
        ? $"Selected: {SelectedItemCount:N0} files · {FormatSize(SelectedItemSizeMegabytes)}"
        : "Selected: none";

    public bool IsCurrentCategoryFullySelected
    {
        get => SelectedTarget?.IsFullySelected ?? false;
        set
        {
            if (SelectedTarget is null)
            {
                return;
            }

            SelectedTarget.IsFullySelected = value;
            OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        }
    }

    public bool HasPendingDeletion => PendingDeletionItemCount > 0;

    public string PendingDeletionSizeDisplay => FormatSize(PendingDeletionTotalSizeMegabytes);

    public string PendingDeletionItemSummary => PendingDeletionItemCount == 1
        ? "1 item"
        : $"{PendingDeletionItemCount:N0} items";

    public string PendingDeletionCategorySummary => PendingDeletionCategoryCount == 1
        ? "1 category"
        : $"{PendingDeletionCategoryCount:N0} categories";

    public string PendingDeletionCategoryListDisplay => string.IsNullOrWhiteSpace(PendingDeletionCategoryList)
        ? "—"
        : PendingDeletionCategoryList;

    public bool HasPendingDeletionRisks => PendingDeletionRisks.Count > 0;

    [RelayCommand]
    private async Task RunPreviewAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ClearTargets();
            CurrentPage = 1;

            var report = await _cleanupService.PreviewAsync(IncludeDownloads, PreviewCount, SelectedItemKind);
            foreach (var target in report.Targets.OrderByDescending(t => t.TotalSizeBytes))
            {
                AddTargetGroup(new CleanupTargetGroupViewModel(target));
            }

            if (Targets.Count > 0)
            {
                SelectedTarget = Targets[0];
            }

            CurrentPhase = CleanupPhase.Preview;

            var status = Targets.Count == 0
                ? "No cleanup targets detected."
                : $"Preview ready: {SummaryText}";

            var warningCount = report.Targets.Sum(static target => target.Warnings.Count);
            if (warningCount > 0)
            {
                status += warningCount == 1
                    ? " • 1 warning"
                    : $" • {warningCount} warnings";
            }

            _mainViewModel.SetStatusMessage(status);
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Cleanup preview failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectionSummaryText));
            OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedAsync()
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }

        var itemsToDelete = Targets
            .SelectMany(static group => group.SelectedItems.Select(item => (group, item)))
            .ToList();

        if (itemsToDelete.Count == 0)
        {
            return Task.CompletedTask;
        }

        PrepareDeletionConfirmation(itemsToDelete);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void DismissDeletionConfirmation()
    {
        ClearPendingDeletionState();
    }

    [RelayCommand]
    private void NavigateToSetup()
    {
        ClearPendingDeletionState();
        CurrentPhase = CleanupPhase.Setup;
    }

    private bool CanConfirmCleanup() => !IsBusy && _pendingDeletionItems is { Count: > 0 } && IsConfirmationSheetVisible;

    [RelayCommand(CanExecute = nameof(CanConfirmCleanup))]
    private async Task ConfirmCleanupAsync()
    {
        if (_pendingDeletionItems is null || _pendingDeletionItems.Count == 0)
        {
            ClearPendingDeletionState();
            return;
        }

        var snapshot = _pendingDeletionItems
            .Where(static tuple => tuple.group.Items.Contains(tuple.item))
            .ToList();

        var useRecycleBin = UseRecycleBin;
        var generateReport = GenerateCleanupReport;

        ClearPendingDeletionState();

        if (snapshot.Count == 0)
        {
            _mainViewModel.SetStatusMessage("Deletion cancelled — no items remain selected.");
            return;
        }

        var deletionOptions = new CleanupDeletionOptions
        {
            PreferRecycleBin = useRecycleBin,
            AllowPermanentDeleteFallback = true
        };

        await ExecuteDeletionAsync(snapshot, deletionOptions, generateReport);
    }

    private bool CanDeleteSelected() => !IsBusy && HasSelection && !IsConfirmationSheetVisible;

    private void PrepareDeletionConfirmation(List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> itemsToDelete)
    {
        _pendingDeletionItems = itemsToDelete;
        PendingDeletionItemCount = itemsToDelete.Count;
        PendingDeletionTotalSizeMegabytes = itemsToDelete.Sum(static tuple => tuple.item.SizeMegabytes);

        var categoryNames = itemsToDelete
            .Select(static tuple => tuple.group.Category)
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        PendingDeletionCategoryCount = categoryNames.Count;

        var displayCategories = categoryNames.Count <= 4
            ? categoryNames
            : categoryNames.Take(4).Concat(new[] { "..." }).ToList();

        PendingDeletionCategoryList = displayCategories.Count == 0
            ? string.Empty
            : string.Join(", ", displayCategories);

        UseRecycleBin = true;
        GenerateCleanupReport = false;

        BuildPendingDeletionRisks(itemsToDelete);

        IsConfirmationSheetVisible = true;
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
    }

    private void ClearPendingDeletionState()
    {
        _pendingDeletionItems = null;
        if (IsConfirmationSheetVisible)
        {
            IsConfirmationSheetVisible = false;
        }

        PendingDeletionRisks.Clear();
        PendingDeletionItemCount = 0;
        PendingDeletionTotalSizeMegabytes = 0;
        PendingDeletionCategoryCount = 0;
        PendingDeletionCategoryList = string.Empty;
        OnPropertyChanged(nameof(HasPendingDeletionRisks));
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void BuildPendingDeletionRisks(IEnumerable<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> items)
    {
        PendingDeletionRisks.Clear();

        var materialized = items.ToList();
        if (materialized.Count == 0)
        {
            OnPropertyChanged(nameof(HasPendingDeletionRisks));
            return;
        }

        var recentThresholdUtc = DateTime.UtcNow - TimeSpan.FromDays(3);
        var recentItems = materialized.Count(tuple =>
        {
            var lastModified = tuple.item.Model.LastModifiedUtc;
            if (lastModified == DateTime.MinValue)
            {
                return tuple.item.Model.WasModifiedRecently;
            }

            return lastModified >= recentThresholdUtc;
        });

        if (recentItems > 0)
        {
            PendingDeletionRisks.Add(new CleanupDeletionRiskViewModel(
                "Recently modified files",
                $"{recentItems:N0} item(s) were updated within the last 3 days.",
                CleanupDeletionRiskSeverity.Caution));
        }

        var systemItems = materialized.Count(tuple =>
        {
            if (tuple.item.IsSystem)
            {
                return true;
            }

            var path = tuple.item.Model.FullName;
            return IsElevationLikelyRequired(path);
        });

        if (systemItems > 0)
        {
            PendingDeletionRisks.Add(new CleanupDeletionRiskViewModel(
                "Protected locations",
                $"{systemItems:N0} item(s) live in system or protected directories. Administrator rights may be required.",
                CleanupDeletionRiskSeverity.Danger));
        }

        var lockedItems = materialized.Count(tuple =>
            tuple.item.Signals.Any(static signal =>
                signal.IndexOf("handle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                signal.IndexOf("in use", StringComparison.OrdinalIgnoreCase) >= 0));

        if (lockedItems > 0)
        {
            PendingDeletionRisks.Add(new CleanupDeletionRiskViewModel(
                "Items in use",
                $"{lockedItems:N0} item(s) appear locked by other processes; they will be skipped automatically if busy.",
                CleanupDeletionRiskSeverity.Caution));
        }

        OnPropertyChanged(nameof(HasPendingDeletionRisks));
    }

    private async Task ExecuteDeletionAsync(
        IReadOnlyList<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> itemsToDelete,
        CleanupDeletionOptions deletionOptions,
        bool generateReport)
    {
        var totalSizeMb = itemsToDelete.Sum(static tuple => tuple.item.SizeMegabytes);

        var requiresElevation = itemsToDelete.Any(static tuple => IsElevationLikelyRequired(tuple.item.Model.FullName));
        if (requiresElevation && _privilegeService.CurrentMode != PrivilegeMode.Administrator)
        {
            if (ConfirmElevation is not null && !ConfirmElevation.Invoke("Deleting some of these items may need administrator permission. Restart with admin rights?"))
            {
                _mainViewModel.SetStatusMessage("Deletion requires administrator rights; cancelled by user.");
                return;
            }

            var restartResult = _privilegeService.Restart(PrivilegeMode.Administrator);
            if (restartResult.Success)
            {
                _mainViewModel.SetStatusMessage("Restarting with administrator privileges...");
                AdministratorRestartRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (restartResult.AlreadyInTargetMode)
            {
                _mainViewModel.SetStatusMessage("Already running with administrator privileges.");
            }
            else
            {
                _mainViewModel.SetStatusMessage(restartResult.ErrorMessage ?? "Unable to restart with administrator privileges.");
            }
        }

        var models = itemsToDelete.Select(static tuple => tuple.item.Model).ToList();
        if (models.Count == 0)
        {
            _mainViewModel.SetStatusMessage("Cleanup cancelled — nothing remains selected.");
            return;
        }

        try
        {
            IsDeleting = true;
            IsBusy = true;
            DeletionProgressTotal = models.Count;
            DeletionProgressCurrent = 0;
            DeletionStatusMessage = totalSizeMb > 0
                ? $"Removing {models.Count:N0} item(s) • {totalSizeMb:F2} MB"
                : $"Removing {models.Count:N0} item(s)";

            var progress = new Progress<CleanupDeletionProgress>(report =>
            {
                DeletionProgressCurrent = report.Completed;
                DeletionProgressTotal = report.Total;
                DeletionStatusMessage = string.IsNullOrEmpty(report.CurrentPath)
                    ? $"Deleting {report.Completed} of {report.Total}"
                    : $"Deleting {report.Completed}/{report.Total}: {report.CurrentPath}";
            });

            var deletionResult = await _cleanupService.DeleteAsync(models, progress, deletionOptions);

            foreach (var (group, item) in itemsToDelete)
            {
                group.Items.Remove(item);
            }

            foreach (var emptyGroup in Targets.Where(static group => group.RemainingItemCount == 0).ToList())
            {
                RemoveTargetGroup(emptyGroup);
            }

            RefreshFilteredItems();
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(SelectedItemCount));
            OnPropertyChanged(nameof(SelectedItemSizeMegabytes));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionSummaryText));
            OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));

            var deletionSummary = deletionResult.ToStatusMessage();
            if (generateReport)
            {
                var reportPath = TryGenerateCleanupReport(deletionResult, out var reportError);
                if (!string.IsNullOrWhiteSpace(reportPath))
                {
                    deletionSummary += $" • Report saved to {reportPath}";
                }
                else if (!string.IsNullOrWhiteSpace(reportError))
                {
                    deletionSummary += $" • Report failed: {reportError}";
                }
            }

            _mainViewModel.SetStatusMessage(deletionSummary);

            if (deletionResult.HasErrors && deletionResult.Errors.Count > 0)
            {
                DeletionStatusMessage = deletionSummary + " • " + deletionResult.Errors[0];
            }
            else
            {
                DeletionStatusMessage = deletionSummary;
            }
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Delete failed: {ex.Message}");
            DeletionStatusMessage = ex.Message;
        }
        finally
        {
            IsDeleting = false;
            IsBusy = false;
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            ConfirmCleanupCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectionSummaryText));
            OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        }
    }

    private string? TryGenerateCleanupReport(CleanupDeletionResult result, out string? error)
    {
        error = null;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TidyWindow", "Reports");
            Directory.CreateDirectory(root);

            var fileName = $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            var filePath = Path.Combine(root, fileName);

            var builder = new StringBuilder();
            builder.AppendLine("TidyWindow cleanup report");
            builder.AppendLine($"Generated: {DateTime.Now:G}");
            builder.AppendLine();
            builder.AppendLine("Summary");
            builder.AppendLine($"  Deleted: {result.DeletedCount:N0}");
            builder.AppendLine($"  Skipped: {result.SkippedCount:N0}");
            builder.AppendLine($"  Failed : {result.FailedCount:N0}");
            builder.AppendLine($"  Space reclaimed: {FormatSize(result.TotalBytesDeleted / 1_048_576d)}");
            builder.AppendLine();

            foreach (var entry in result.Entries)
            {
                builder.AppendLine($"{entry.Disposition}: {entry.Path}");
                if (!string.IsNullOrWhiteSpace(entry.Reason))
                {
                    builder.AppendLine("  " + entry.Reason);
                }
                builder.AppendLine();
            }

            File.WriteAllText(filePath, builder.ToString());
            return filePath;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllCurrent))]
    private void SelectAllCurrent()
    {
        var target = SelectedTarget;
        if (target is null || FilteredItems.Count == 0)
        {
            return;
        }

        using (target.BeginSelectionUpdate())
        {
            foreach (var item in FilteredItems)
            {
                item.IsSelected = true;
            }
        }
    }

    private bool CanSelectAllCurrent() => FilteredItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSelectAcrossPages))]
    private void SelectAllPages()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            return;
        }

        using (target.BeginSelectionUpdate())
        {
            foreach (var item in target.Items.Where(MatchesFilters))
            {
                item.IsSelected = true;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAcrossPages))]
    private void SelectPageRange()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            return;
        }

        var totalPages = TotalPages;
        if (totalPages <= 0)
        {
            return;
        }

        var start = SelectRangeStartPage;
        var end = SelectRangeEndPage;

        if (start > end)
        {
            (start, end) = (end, start);
        }

        start = Math.Clamp(start, 1, totalPages);
        end = Math.Clamp(end, 1, totalPages);

        if (start > end)
        {
            return;
        }

        var startIndex = (start - 1) * PageSize;
        var endExclusive = end * PageSize;
        var index = 0;

        using (target.BeginSelectionUpdate())
        {
            foreach (var item in target.Items.Where(MatchesFilters))
            {
                if (index >= startIndex && index < endExclusive)
                {
                    item.IsSelected = true;
                }
                else if (index >= endExclusive)
                {
                    break;
                }

                index++;
            }
        }
    }

    private bool CanSelectAcrossPages()
    {
        return SelectedTarget is not null && _totalFilteredItems > 0;
    }

    [RelayCommand(CanExecute = nameof(CanClearCurrentSelection))]
    private void ClearCurrentSelection()
    {
        var target = SelectedTarget;
        if (target is null || FilteredItems.Count == 0)
        {
            return;
        }

        using (target.BeginSelectionUpdate())
        {
            foreach (var item in FilteredItems)
            {
                item.IsSelected = false;
            }
        }
    }

    private bool CanClearCurrentSelection() => FilteredItems.Any(static item => item.IsSelected);

    [RelayCommand]
    private void ResetFilters()
    {
        IncludeDownloads = true;
        SelectedItemKind = CleanupItemKind.Both;
        SelectedExtensionFilterMode = CleanupExtensionFilterMode.None;
        SelectedExtensionProfile = ExtensionProfiles.FirstOrDefault();
        CustomExtensionInput = string.Empty;
        PreviewCount = DefaultPreviewCount;
        SelectRangeStartPage = 1;
        SelectRangeEndPage = 1;
        PreviewSortMode = CleanupPreviewSortMode.Impact;
        _mainViewModel.SetStatusMessage("Cleanup filters reset to defaults.");
        RefreshFilteredItems();
    }

    [RelayCommand]
    private void Cancel()
    {
        var destination = _mainViewModel.NavigationItems.FirstOrDefault();
        if (destination is not null)
        {
            _mainViewModel.NavigateTo(destination.PageType);
        }

        _mainViewModel.SetStatusMessage("Cleanup setup cancelled. Returning to dashboard.");
    }

    [RelayCommand]
    private void ApplyPreviewPreset(int value)
    {
        PreviewCount = value;
    }

    [RelayCommand]
    private void SetPreviewSortMode(CleanupPreviewSortMode mode)
    {
        PreviewSortMode = mode;
    }

    partial void OnIsBusyChanged(bool value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetChanged(CleanupTargetGroupViewModel? oldValue, CleanupTargetGroupViewModel? newValue)
    {
        if (oldValue is not null)
        {
            using (oldValue.BeginSelectionUpdate())
            {
                foreach (var item in oldValue.Items)
                {
                    item.IsSelected = false;
                }
            }
        }

        if (newValue is null || newValue.Items.Count == 0)
        {
            RefreshFilteredItems();
            SelectAllCurrentCommand.NotifyCanExecuteChanged();
            ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
            SelectAllPagesCommand.NotifyCanExecuteChanged();
            SelectPageRangeCommand.NotifyCanExecuteChanged();
            return;
        }

        RefreshFilteredItems();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        SelectAllPagesCommand.NotifyCanExecuteChanged();
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedItemKindChanged(CleanupItemKind value)
    {
        RefreshFilteredItems();
    }

    partial void OnPendingDeletionItemCountChanged(int value)
    {
        OnPropertyChanged(nameof(PendingDeletionItemSummary));
        OnPropertyChanged(nameof(HasPendingDeletion));
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingDeletionTotalSizeMegabytesChanged(double value)
    {
        OnPropertyChanged(nameof(PendingDeletionSizeDisplay));
    }

    partial void OnPendingDeletionCategoryCountChanged(int value)
    {
        OnPropertyChanged(nameof(PendingDeletionCategorySummary));
    }

    partial void OnPendingDeletionCategoryListChanged(string value)
    {
        OnPropertyChanged(nameof(PendingDeletionCategoryListDisplay));
    }

    partial void OnIsConfirmationSheetVisibleChanged(bool value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentPhaseChanged(CleanupPhase value)
    {
        OnPropertyChanged(nameof(IsSetupPhase));
        OnPropertyChanged(nameof(IsPreviewPhase));
    }

    partial void OnSelectedExtensionFilterModeChanged(CleanupExtensionFilterMode value)
    {
        RebuildExtensionCache();
        OnPropertyChanged(nameof(IsExtensionSelectorEnabled));
        RefreshFilteredItems();
    }

    partial void OnSelectedExtensionProfileChanged(CleanupExtensionProfile? value)
    {
        RebuildExtensionCache();
        RefreshFilteredItems();
    }

    partial void OnCustomExtensionInputChanged(string value)
    {
        RebuildExtensionCache();
        RefreshFilteredItems();
    }

    partial void OnSelectRangeStartPageChanged(int value)
    {
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectRangeEndPageChanged(int value)
    {
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(SelectedItemSizeMegabytes));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnGroupItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshFilteredItems();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectAllPagesCommand.NotifyCanExecuteChanged();
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    private void ClearTargets()
    {
        foreach (var group in Targets.ToList())
        {
            RemoveTargetGroup(group);
        }

        SelectedTarget = null;
        FilteredItems.Clear();
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(SelectedItemSizeMegabytes));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        OnPropertyChanged(nameof(HasFilteredResults));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectAllPagesCommand.NotifyCanExecuteChanged();
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    private void AddTargetGroup(CleanupTargetGroupViewModel group)
    {
        group.SelectionChanged += OnGroupSelectionChanged;
        group.ItemsChanged += OnGroupItemsChanged;
        Targets.Add(group);
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        SelectAllPagesCommand.NotifyCanExecuteChanged();
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    private void RemoveTargetGroup(CleanupTargetGroupViewModel group)
    {
        group.SelectionChanged -= OnGroupSelectionChanged;
        group.ItemsChanged -= OnGroupItemsChanged;
        Targets.Remove(group);
        group.Dispose();
        if (ReferenceEquals(SelectedTarget, group))
        {
            SelectedTarget = Targets.FirstOrDefault();
        }

        RefreshFilteredItems();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        SelectAllPagesCommand.NotifyCanExecuteChanged();
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    private void RefreshFilteredItems()
    {
        FilteredItems.Clear();
        if (SelectedTarget is null)
        {
            _totalFilteredItems = 0;
            OnPropertyChanged(nameof(HasFilteredResults));
            OnPropertyChanged(nameof(ExtensionStatusText));
            OnPropertyChanged(nameof(PageDisplay));
            OnPropertyChanged(nameof(TotalPages));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
            SelectAllCurrentCommand.NotifyCanExecuteChanged();
            ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
            SelectAllPagesCommand.NotifyCanExecuteChanged();
            SelectPageRangeCommand.NotifyCanExecuteChanged();
            return;
        }
        // Get all filtered items, but only show current page
        var filteredQuery = SelectedTarget.Items.Where(MatchesFilters);
        filteredQuery = PreviewSortMode switch
        {
            CleanupPreviewSortMode.Impact => filteredQuery.OrderByDescending(static item => item.SizeBytes),
            CleanupPreviewSortMode.Newest => filteredQuery.OrderByDescending(static item => item.LastModifiedLocal),
            CleanupPreviewSortMode.Risk => filteredQuery.OrderBy(static item => item.Confidence),
            _ => filteredQuery
        };

        var filtered = filteredQuery.ToList();
        _totalFilteredItems = filtered.Count;
        int skip = (CurrentPage - 1) * PageSize;
        foreach (var item in filtered.Skip(skip).Take(PageSize))
        {
            FilteredItems.Add(item);
        }
        OnPropertyChanged(nameof(HasFilteredResults));
        OnPropertyChanged(nameof(ExtensionStatusText));
        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        SelectAllPagesCommand.NotifyCanExecuteChanged();
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    private bool MatchesFilters(CleanupPreviewItemViewModel item)
    {
        switch (SelectedItemKind)
        {
            case CleanupItemKind.Files when item.IsDirectory:
                return false;
            case CleanupItemKind.Folders when !item.IsDirectory:
                return false;
        }

        if (item.IsDirectory)
        {
            if (SelectedItemKind == CleanupItemKind.Files)
            {
                return false;
            }

            if (SelectedExtensionFilterMode == CleanupExtensionFilterMode.IncludeOnly && _activeExtensions.Count > 0)
            {
                return false;
            }

            return true;
        }

        if (SelectedExtensionFilterMode == CleanupExtensionFilterMode.None || _activeExtensions.Count == 0)
        {
            return true;
        }

        var extension = NormalizeExtension(item.Extension);
        return SelectedExtensionFilterMode switch
        {
            CleanupExtensionFilterMode.IncludeOnly => _activeExtensions.Contains(extension),
            CleanupExtensionFilterMode.Exclude => !_activeExtensions.Contains(extension),
            _ => true
        };
    }

    private void RebuildExtensionCache()
    {
        _activeExtensions.Clear();

        if (SelectedExtensionFilterMode == CleanupExtensionFilterMode.None)
        {
            return;
        }

        if (SelectedExtensionProfile?.Extensions is { } presetExtensions)
        {
            foreach (var preset in presetExtensions)
            {
                var normalized = NormalizeExtension(preset);
                if (!string.IsNullOrEmpty(normalized))
                {
                    _activeExtensions.Add(normalized);
                }
            }
        }

        foreach (var entry in ParseExtensions(CustomExtensionInput))
        {
            var normalized = NormalizeExtension(entry);
            if (!string.IsNullOrEmpty(normalized))
            {
                _activeExtensions.Add(normalized);
            }
        }
    }

    private static string FormatSize(double megabytes)
    {
        if (megabytes >= 1024d)
        {
            return $"{megabytes / 1024d:F2} GB";
        }

        if (megabytes >= 1d)
        {
            return $"{megabytes:F2} MB";
        }

        return $"{megabytes * 1024d:F0} KB";
    }

    private static IEnumerable<string> ParseExtensions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var tokens = value.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            yield return token;
        }
    }

    private static string[] BuildSensitiveRoots()
    {
        var roots = new List<string>();

        void AddIfExists(params Environment.SpecialFolder[] folders)
        {
            foreach (var folder in folders)
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    roots.Add(path);
                }
            }
        }

        AddIfExists(Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Temp"));

        return roots.Where(static directory => !string.IsNullOrWhiteSpace(directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsElevationLikelyRequired(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var root in _sensitiveRoots)
        {
            if (!string.IsNullOrWhiteSpace(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        if (!trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            trimmed = "." + trimmed;
        }

        return trimmed.ToLowerInvariant();
    }
}
