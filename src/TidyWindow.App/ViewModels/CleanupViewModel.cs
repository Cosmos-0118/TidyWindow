using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels.Preview;
using TidyWindow.Core.Cleanup;
using WindowsClipboard = System.Windows.Clipboard;

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

public sealed record CleanupAgeFilterOption(int Days, string Label, string Description)
{
    public override string ToString() => Label;
}

public enum CleanupPhase
{
    Setup,
    Preview,
    Celebration
}

public enum CleanupDeletionRiskSeverity
{
    Info,
    Caution,
    Danger
}

public sealed record CleanupDeletionRiskViewModel(string Title, string Description, CleanupDeletionRiskSeverity Severity);

public sealed partial class CleanupCelebrationFailureViewModel : ObservableObject
{
    public CleanupCelebrationFailureViewModel(
        CleanupTargetGroupViewModel group,
        CleanupPreviewItemViewModel item,
        CleanupDeletionEntry entry)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public CleanupTargetGroupViewModel Group { get; }

    public CleanupPreviewItemViewModel Item { get; }

    public CleanupDeletionEntry Entry { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Item.Name) ? Item.FullName : Item.Name;

    public string Category => Group.Category;

    public CleanupDeletionDisposition Disposition => Entry.Disposition;

    public string Reason => Entry.EffectiveReason;

    [ObservableProperty]
    private bool _isRetrying;
}

public sealed partial class CleanupLockProcessViewModel : ObservableObject
{
    private static readonly IReadOnlyList<string> EmptyPaths = Array.Empty<string>();

    public CleanupLockProcessViewModel(ResourceLockInfo info)
    {
        if (info is null)
        {
            throw new ArgumentNullException(nameof(info));
        }

        Handle = info.Handle;
        DisplayName = string.IsNullOrWhiteSpace(info.DisplayName)
            ? $"Process {info.ProcessId}"
            : info.DisplayName;
        Description = string.IsNullOrWhiteSpace(info.Description)
            ? "Application"
            : info.Description;
        IsService = info.IsService;
        IsCritical = info.IsCritical;
        IsRestartable = info.IsRestartable;
        ResourcePaths = info.ResourcePaths?.Count > 0 ? info.ResourcePaths : EmptyPaths;
    }

    public ResourceLockHandle Handle { get; }

    public int ProcessId => Handle.ProcessId;

    public string DisplayName { get; }

    public string Description { get; }

    public bool IsService { get; }

    public bool IsCritical { get; }

    public bool IsRestartable { get; }

    public IReadOnlyList<string> ResourcePaths { get; }

    public int ImpactedItemCount => ResourcePaths.Count;

    public string ImpactSummary
    {
        get
        {
            if (ImpactedItemCount == 0)
            {
                return "Potentially locking selected files";
            }

            if (ImpactedItemCount == 1)
            {
                return TrimPath(ResourcePaths[0]);
            }

            return $"Impacting {ImpactedItemCount:N0} selected items";
        }
    }

    [ObservableProperty]
    private bool _isSelected = true;

    private static string TrimPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Selected item";
        }

        const int prefixLength = 22;
        const int suffixLength = 18;
        if (path.Length <= prefixLength + suffixLength + 3)
        {
            return path;
        }

        return string.Concat(path.AsSpan(0, prefixLength), "…", path.AsSpan(path.Length - suffixLength));
    }
}

public sealed partial class CleanupViewModel : ViewModelBase
{
    private readonly CleanupService _cleanupService;
    private readonly MainViewModel _mainViewModel;
    private readonly IPrivilegeService _privilegeService;
    private readonly IResourceLockService _resourceLockService;
    private readonly IBrowserCleanupService _browserCleanupService;

    private const int PreviewCountMinimumValue = 10;
    private const int PreviewCountMaximumValue = 100_000;
    private const int DefaultPreviewCount = 50;
    private const int MaxLockInspectionItemsPerCategory = 32;
    private const int MaxLockInspectionSampleTotal = 600;

    private readonly HashSet<string> _activeExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CleanupPreviewFilter _previewFilter;
    private readonly PreviewPagingController _previewPagingController;
    private int _previewCount = DefaultPreviewCount;
    private static readonly string[] _sensitiveRoots = BuildSensitiveRoots();
    private List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)>? _pendingDeletionItems;
    private int _lastPreviewTotalItemCount;
    private bool _hasCompletedPreview;
    private CancellationTokenSource? _refreshToastCancellation;
    private CancellationTokenSource? _phaseTransitionCancellation;
    private CancellationTokenSource? _lockInspectionCancellation;
    private readonly TimeSpan _phaseTransitionLeadDuration = TimeSpan.FromMilliseconds(160);
    private readonly TimeSpan _phaseTransitionSettleDuration = TimeSpan.FromMilliseconds(220);
    private const int DeletionUiYieldInterval = 750;
    private LockInspectionSampleStats _lastLockInspectionStats = new(0, 0, 0);
    private DateTime? _minimumAgeThresholdUtc;

    public CleanupViewModel(CleanupService cleanupService, MainViewModel mainViewModel, IPrivilegeService privilegeService, IResourceLockService resourceLockService, IBrowserCleanupService browserCleanupService)
    {
        _cleanupService = cleanupService;
        _mainViewModel = mainViewModel;
        _privilegeService = privilegeService;
        _resourceLockService = resourceLockService;
        _browserCleanupService = browserCleanupService;

        _previewFilter = new CleanupPreviewFilter
        {
            SelectedItemKind = _selectedItemKind,
            ExtensionFilterMode = _selectedExtensionFilterMode,
            MinimumAgeThresholdUtc = _minimumAgeThresholdUtc
        };
        _previewPagingController = new PreviewPagingController(() => SelectedTarget, _previewFilter);
        _previewPagingController.StateChanged += OnPreviewPagingStateChanged;

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

        AgeFilterOptions = new List<CleanupAgeFilterOption>
        {
            new(0, "Include all items", "Show every result regardless of the last modified date."),
            new(7, "Older than 7 days", "Hide files touched in the last week."),
            new(30, "Older than 30 days", "Great for trimming browser history and recent downloads."),
            new(90, "Older than 90 days", "Focus on quarterly clutter only."),
            new(180, "Older than 180 days", "Surface only long-lived files you likely forgot about."),
        };

        SelectedExtensionProfile = ExtensionProfiles.FirstOrDefault();
        SelectedAgeFilter = AgeFilterOptions.FirstOrDefault();
        RebuildExtensionCache();
        CelebrationFailures.CollectionChanged += OnCelebrationFailuresCollectionChanged;
        PendingDeletionCategories = new ReadOnlyObservableCollection<string>(_pendingDeletionCategories);
        CelebrationCategories = new ReadOnlyObservableCollection<string>(_celebrationCategories);
        _pendingDeletionCategories.CollectionChanged += OnPendingDeletionCategoriesCollectionChanged;
        _celebrationCategories.CollectionChanged += OnCelebrationCategoriesCollectionChanged;
        LockingProcesses.CollectionChanged += OnLockingProcessesCollectionChanged;
    }

    [ObservableProperty]
    private bool _includeDownloads = true;

    [ObservableProperty]
    private bool _includeBrowserHistory = true;

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
    private CleanupAgeFilterOption? _selectedAgeFilter;

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
    private string _busyStatusMessage = "Working…";

    [ObservableProperty]
    private string _busyStatusDetail = string.Empty;

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
    private bool _skipLockedItems = true;

    [ObservableProperty]
    private bool _repairPermissionsBeforeDelete;

    [ObservableProperty]
    private bool _scheduleLockedItemsForReboot;

    [ObservableProperty]
    private CleanupPhase _currentPhase = CleanupPhase.Setup;

    [ObservableProperty]
    private string _celebrationHeadline = "Cleanup complete";

    [ObservableProperty]
    private string _celebrationDetails = string.Empty;

    [ObservableProperty]
    private double _celebrationReclaimedMegabytes;

    [ObservableProperty]
    private int _celebrationItemsDeleted;

    [ObservableProperty]
    private int _celebrationItemsSkipped;

    [ObservableProperty]
    private int _celebrationItemsFailed;

    [ObservableProperty]
    private int _celebrationCategoryCount;

    [ObservableProperty]
    private string _celebrationCategoryList = string.Empty;

    [ObservableProperty]
    private string _celebrationTimeSavedDisplay = string.Empty;

    [ObservableProperty]
    private string _celebrationDurationDisplay = string.Empty;

    [ObservableProperty]
    private string _celebrationShareSummary = string.Empty;

    [ObservableProperty]
    private string? _celebrationReportPath;

    [ObservableProperty]
    private bool _isRefreshToastVisible;

    [ObservableProperty]
    private string _refreshToastText = string.Empty;

    [ObservableProperty]
    private bool _isPhaseTransitioning;

    [ObservableProperty]
    private string _phaseTransitionMessage = string.Empty;

    [ObservableProperty]
    private bool _isLockInspectionInProgress;

    [ObservableProperty]
    private bool _isClosingLockingProcesses;

    [ObservableProperty]
    private string _lockInspectionStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLockingProcessPopupOpen;

    partial void OnIsLockInspectionInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLockingProcessPanelVisible));
        OnPropertyChanged(nameof(LockingProcessSummary));
        OnPropertyChanged(nameof(LockingProcessButtonLabel));
    }

    partial void OnLockInspectionStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(LockingProcessSummary));
    }

    partial void OnIsClosingLockingProcessesChanged(bool value)
    {
        CloseSelectedLockingProcessesCommand.NotifyCanExecuteChanged();
        ForceCloseSelectedLockingProcessesCommand.NotifyCanExecuteChanged();
        CloseAllLockingProcessesCommand.NotifyCanExecuteChanged();
    }

    private readonly ObservableCollection<string> _pendingDeletionCategories = new();
    private readonly ObservableCollection<string> _celebrationCategories = new();

    public ObservableCollection<CleanupTargetGroupViewModel> Targets { get; } = new();

    public ObservableCollection<CleanupPreviewItemViewModel> FilteredItems => _previewPagingController.FilteredItems;

    public ObservableCollection<CleanupDeletionRiskViewModel> PendingDeletionRisks { get; } = new();

    public ObservableCollection<CleanupCelebrationFailureViewModel> CelebrationFailures { get; } = new();

    public ObservableCollection<CleanupLockProcessViewModel> LockingProcesses { get; } = new();

    public ReadOnlyObservableCollection<string> PendingDeletionCategories { get; }

    public ReadOnlyObservableCollection<string> CelebrationCategories { get; }

    public bool HasLockingProcesses => LockingProcesses.Count > 0;

    public bool IsLockingProcessPanelVisible => IsLockInspectionInProgress || HasLockingProcesses || !string.IsNullOrWhiteSpace(LockInspectionStatusMessage);

    public string LockingProcessSummary
    {
        get
        {
            if (IsLockInspectionInProgress)
            {
                return string.IsNullOrWhiteSpace(LockInspectionStatusMessage)
                    ? "Scanning for running apps…"
                    : LockInspectionStatusMessage;
            }

            if (!HasLockingProcesses)
            {
                return string.IsNullOrWhiteSpace(LockInspectionStatusMessage)
                    ? "No running apps are locking the selected files."
                    : LockInspectionStatusMessage;
            }

            var blockingAppCount = LockingProcesses.Count;
            var impactedItems = LockingProcesses.Sum(static vm => Math.Max(1, vm.ImpactedItemCount));
            var appLabel = blockingAppCount == 1 ? "app" : "apps";
            var itemLabel = impactedItems == 1 ? "item" : "items";
            return $"{blockingAppCount:N0} {appLabel} may be locking {impactedItems:N0} {itemLabel}.";
        }
    }

    public string LockingProcessButtonLabel
    {
        get
        {
            if (IsLockInspectionInProgress)
            {
                return "Scanning…";
            }

            if (HasLockingProcesses)
            {
                return $"View apps ({LockingProcesses.Count:N0})";
            }

            return "View apps";
        }
    }

    public int CurrentPage
    {
        get => _previewPagingController.CurrentPage;
        set
        {
            if (_previewPagingController.TrySetCurrentPage(value))
            {
                OnPropertyChanged(nameof(CurrentPage));
            }
        }
    }

    public int PageSize
    {
        get => _previewPagingController.PageSize;
        set
        {
            if (_previewPagingController.TrySetPageSize(value))
            {
                OnPropertyChanged(nameof(PageSize));
            }
        }
    }

    public int TotalPages => _previewPagingController.TotalPages;

    public string PageDisplay => _previewPagingController.PageDisplay;

    private void ResetCurrentPage()
    {
        _previewPagingController.ResetCurrentPage();
    }

    public bool CanGoToPreviousPage => _previewPagingController.CanGoToPreviousPage;

    public bool CanGoToNextPage => _previewPagingController.CanGoToNextPage;

    public int SelectRangeStartPage
    {
        get => _previewPagingController.SelectRangeStartPage;
        set
        {
            if (_previewPagingController.TrySetSelectRangeStartPage(value))
            {
                OnPropertyChanged(nameof(SelectRangeStartPage));
            }
        }
    }

    public int SelectRangeEndPage
    {
        get => _previewPagingController.SelectRangeEndPage;
        set
        {
            if (_previewPagingController.TrySetSelectRangeEndPage(value))
            {
                OnPropertyChanged(nameof(SelectRangeEndPage));
            }
        }
    }

    public CleanupPreviewSortMode PreviewSortMode
    {
        get => _previewPagingController.PreviewSortMode;
        set
        {
            if (_previewPagingController.TrySetPreviewSortMode(value))
            {
                OnPropertyChanged(nameof(PreviewSortMode));
            }
        }
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!_previewPagingController.TryGoToNextPage())
        {
            return;
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (!_previewPagingController.TryGoToPreviousPage())
        {
            return;
        }
    }

    public IReadOnlyList<CleanupItemKindOption> ItemKindOptions { get; }

    public IReadOnlyList<CleanupExtensionFilterOption> ExtensionFilterOptions { get; }

    public IReadOnlyList<CleanupExtensionProfile> ExtensionProfiles { get; }

    public IReadOnlyList<CleanupPreviewSortOption> SortOptions { get; }

    public IReadOnlyList<CleanupAgeFilterOption> AgeFilterOptions { get; }

    public event EventHandler? AdministratorRestartRequested;

    public Func<string, bool>? ConfirmElevation { get; set; }

    public bool HasResults => Targets.Count > 0;

    public bool HasFilteredResults => FilteredItems.Count > 0;

    public bool IsSetupPhase => CurrentPhase == CleanupPhase.Setup;

    public bool IsPreviewPhase => CurrentPhase == CleanupPhase.Preview;

    public bool IsCelebrationPhase => CurrentPhase == CleanupPhase.Celebration;

    public bool IsExtensionSelectorEnabled => SelectedExtensionFilterMode != CleanupExtensionFilterMode.None;

    public bool HasCelebrationFailures => CelebrationFailures.Count > 0;

    public bool HasCelebrationCategories => CelebrationCategories.Count > 0;

    public bool CanReviewCelebrationReport => !string.IsNullOrWhiteSpace(CelebrationReportPath) && File.Exists(CelebrationReportPath);

    public string CelebrationReclaimedDisplay => FormatSize(CelebrationReclaimedMegabytes);

    public string CelebrationCategoryListDisplay => string.IsNullOrWhiteSpace(CelebrationCategoryList) ? "—" : CelebrationCategoryList;

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

    public string ActiveOperationStatus => IsDeleting ? DeletionStatusMessage : BusyStatusMessage;

    public string ActiveOperationDetail
    {
        get
        {
            if (IsDeleting)
            {
                if (DeletionProgressTotal > 0)
                {
                    return $"{DeletionProgressCurrent:N0} of {DeletionProgressTotal:N0} items";
                }

                return string.IsNullOrWhiteSpace(DeletionStatusMessage)
                    ? "Preparing deletion plan…"
                    : DeletionStatusMessage;
            }

            return string.IsNullOrWhiteSpace(BusyStatusDetail)
                ? "Hold tight, this step only takes a moment."
                : BusyStatusDetail;
        }
    }

    public int ActiveOperationProgressValue => IsDeleting ? Math.Min(DeletionProgressCurrent, DeletionProgressTotal) : 0;

    public int ActiveOperationProgressMaximum => IsDeleting && DeletionProgressTotal > 0 ? DeletionProgressTotal : 100;

    public bool IsActiveOperationIndeterminate => !IsDeleting || DeletionProgressTotal <= 0;

    public string ActiveOperationPercentDisplay
    {
        get
        {
            if (!IsDeleting || DeletionProgressTotal <= 0)
            {
                return string.Empty;
            }

            var percent = (double)Math.Min(DeletionProgressCurrent, DeletionProgressTotal) / DeletionProgressTotal;
            return percent >= 1d ? "100%" : percent.ToString("P0");
        }
    }

    public bool HasActiveOperationPercent => IsDeleting && DeletionProgressTotal > 0;

    public bool HasPendingDeletion => PendingDeletionItemCount > 0;

    public bool HasPendingDeletionCategories => PendingDeletionCategories.Count > 0;

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
            BusyStatusMessage = "Analyzing cleanup targets…";
            BusyStatusDetail = $"Reviewing up to {PreviewCount:N0} top items";
            ClearTargets();
            CurrentPage = 1;

            var report = await _cleanupService.PreviewAsync(IncludeDownloads, IncludeBrowserHistory, PreviewCount, SelectedItemKind);
            var filteredTargets = FilterPreviewTargets(report.Targets)
                .OrderByDescending(static t => t.TotalSizeBytes)
                .ToList();

            var totalItems = filteredTargets.Sum(static target => target.ItemCount);
            var newItems = _hasCompletedPreview ? Math.Max(0, totalItems - _lastPreviewTotalItemCount) : 0;
            _lastPreviewTotalItemCount = totalItems;

            foreach (var target in filteredTargets)
            {
                AddTargetGroup(new CleanupTargetGroupViewModel(target));
            }

            if (Targets.Count > 0)
            {
                SelectedTarget = Targets[0];
            }

            var phaseMessage = Targets.Count == 0
                ? "Finishing up — no cleanup items detected."
                : "Loading preview results…";

            await TransitionToPhaseAsync(
                CleanupPhase.Preview,
                transitionMessage: phaseMessage);
            HandleRefreshToast(newItems, totalItems);

            var status = Targets.Count == 0
                ? "No cleanup targets detected."
                : $"Preview ready: {SummaryText}";

            var warningCount = filteredTargets.Sum(static target => target.Warnings.Count);
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
            HideRefreshToast();
        }
        finally
        {
            BusyStatusMessage = "Working…";
            BusyStatusDetail = string.Empty;
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
    private async Task NavigateToSetupAsync()
    {
        ClearPendingDeletionState();
        await TransitionToPhaseAsync(
            CleanupPhase.Setup,
            transitionMessage: "Returning to setup options…",
            preTransitionDelay: TimeSpan.FromMilliseconds(120));
        HideRefreshToast();
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
            AllowPermanentDeleteFallback = true,
            SkipLockedItems = SkipLockedItems,
            TakeOwnershipOnAccessDenied = RepairPermissionsBeforeDelete,
            AllowDeleteOnReboot = ScheduleLockedItemsForReboot
        };

        await ExecuteDeletionAsync(snapshot, deletionOptions, generateReport);
    }

    private bool CanDeleteSelected() => !IsBusy && HasSelection && !IsConfirmationSheetVisible;

    private void PrepareDeletionConfirmation(List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> itemsToDelete)
    {
        _pendingDeletionItems = itemsToDelete;
        PendingDeletionItemCount = itemsToDelete.Count;
        PendingDeletionTotalSizeMegabytes = itemsToDelete.Sum(static tuple => tuple.item.SizeMegabytes);

        var categoryNames = NormalizeDistinctCategories(itemsToDelete.Select(static tuple => tuple.group.Category));

        PendingDeletionCategoryCount = categoryNames.Count;
        UpdateCategoryCollection(_pendingDeletionCategories, categoryNames);

        PendingDeletionCategoryList = BuildCategoryListText(categoryNames);

        UseRecycleBin = true;
        GenerateCleanupReport = false;
        SkipLockedItems = true;
        RepairPermissionsBeforeDelete = false;
        ScheduleLockedItemsForReboot = false;

        BuildPendingDeletionRisks(itemsToDelete);

        IsConfirmationSheetVisible = true;
        ConfirmCleanupCommand.NotifyCanExecuteChanged();

        BeginLockInspection(itemsToDelete);
    }

    private void ClearPendingDeletionState()
    {
        CancelLockInspection();
        LockingProcesses.Clear();
        LockInspectionStatusMessage = string.Empty;
        IsLockingProcessPopupOpen = false;
        UpdateLockingProcessSummary();
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
        _pendingDeletionCategories.Clear();
        OnPropertyChanged(nameof(HasPendingDeletionRisks));
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void BeginLockInspection(List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> itemsToInspect)
    {
        CancelLockInspection();
        IsLockingProcessPopupOpen = false;

        if (itemsToInspect is null || itemsToInspect.Count == 0)
        {
            LockingProcesses.Clear();
            LockInspectionStatusMessage = string.Empty;
            UpdateLockingProcessSummary();
            return;
        }

        var sample = BuildLockInspectionSample(itemsToInspect);
        if (sample.Paths.Count == 0)
        {
            LockingProcesses.Clear();
            LockInspectionStatusMessage = "No valid file paths selected for lock inspection.";
            UpdateLockingProcessSummary();
            return;
        }

        LockingProcesses.Clear();
        UpdateLockingProcessSummary();

        var cts = new CancellationTokenSource();
        _lockInspectionCancellation = cts;
        IsLockInspectionInProgress = true;
        _lastLockInspectionStats = sample.Stats;
        LockInspectionStatusMessage = BuildLockInspectionScanMessage(sample.Stats);

        _ = InspectLockingProcessesAsync(sample, cts);
    }

    private LockInspectionSample BuildLockInspectionSample(List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> itemsToInspect)
    {
        if (itemsToInspect is null || itemsToInspect.Count == 0)
        {
            return new LockInspectionSample(Array.Empty<string>(), new LockInspectionSampleStats(0, 0, 0));
        }

        var validItems = itemsToInspect
            .Where(static tuple => !string.IsNullOrWhiteSpace(tuple.item.Model.FullName))
            .ToList();

        if (validItems.Count == 0)
        {
            return new LockInspectionSample(Array.Empty<string>(), new LockInspectionSampleStats(0, 0, 0));
        }

        long totalBytes = 0;
        foreach (var entry in validItems)
        {
            totalBytes += Math.Max(0L, entry.item.Model.SizeBytes);
        }

        var candidateItems = new List<CleanupPreviewItemViewModel>();
        foreach (var group in validItems.GroupBy(static tuple => tuple.group.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var prioritized = group
                .Select(static tuple => tuple.item)
                .OrderByDescending(static item => Math.Max(1L, item.Model.SizeBytes))
                .ThenByDescending(static item => item.Model.LastModifiedUtc)
                .Take(MaxLockInspectionItemsPerCategory);

            candidateItems.AddRange(prioritized);
        }

        candidateItems = candidateItems
            .OrderByDescending(static item => Math.Max(1L, item.Model.SizeBytes))
            .ThenByDescending(static item => item.Model.LastModifiedUtc)
            .ToList();

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedItems = new List<CleanupPreviewItemViewModel>();
        long sampledBytes = 0;

        foreach (var item in candidateItems)
        {
            var path = item.Model.FullName;
            if (string.IsNullOrWhiteSpace(path) || !uniquePaths.Add(path))
            {
                continue;
            }

            selectedItems.Add(item);
            sampledBytes += Math.Max(0L, item.Model.SizeBytes);

            if (selectedItems.Count >= MaxLockInspectionSampleTotal)
            {
                break;
            }
        }

        var selectedPaths = selectedItems.Select(static item => item.Model.FullName).ToList();
        var coverageFraction = totalBytes > 0
            ? Math.Min(1.0, sampledBytes / (double)totalBytes)
            : selectedItems.Count / (double)validItems.Count;

        var stats = new LockInspectionSampleStats(validItems.Count, selectedPaths.Count, coverageFraction);
        return new LockInspectionSample(selectedPaths, stats);
    }

    private static string BuildLockInspectionScanMessage(LockInspectionSampleStats stats)
    {
        if (stats.TotalItems <= 0)
        {
            return "Scanning selected items for locking apps…";
        }

        var itemLabel = stats.TotalItems == 1 ? "item" : "items";
        if (stats.SampledItems >= stats.TotalItems)
        {
            return $"Scanning {stats.SampledItems:N0} selected {itemLabel} for locking apps…";
        }

        var coveragePercent = Math.Clamp(stats.CoverageFraction * 100, 0, 100);
        return coveragePercent > 0
            ? $"Scanning {stats.SampledItems:N0} of {stats.TotalItems:N0} selected {itemLabel} (~{coveragePercent:0}% of total size)…"
            : $"Scanning {stats.SampledItems:N0} of {stats.TotalItems:N0} selected {itemLabel} for locking apps…";
    }

    private static string BuildLockInspectionCoverageSuffix(LockInspectionSampleStats stats)
    {
        if (stats.TotalItems <= 0 || stats.SampledItems >= stats.TotalItems)
        {
            return string.Empty;
        }

        var coveragePercent = Math.Clamp(stats.CoverageFraction * 100, 0, 100);
        return coveragePercent > 0
            ? $" (~{coveragePercent:0}% of selected size)"
            : $" ({stats.SampledItems:N0}/{stats.TotalItems:N0} items sampled)";
    }

    private async Task InspectLockingProcessesAsync(LockInspectionSample sample, CancellationTokenSource cancellationSource)
    {
        try
        {
            var token = cancellationSource.Token;
            var paths = sample.Paths;
            if (paths.Count == 0)
            {
                UpdateLockingProcesses(Array.Empty<ResourceLockInfo>());
                LockInspectionStatusMessage = "No valid file paths selected for lock inspection.";
                return;
            }

            var processes = await _resourceLockService.InspectAsync(paths, token);
            UpdateLockingProcesses(processes);

            var suffix = BuildLockInspectionCoverageSuffix(sample.Stats);
            LockInspectionStatusMessage = processes.Count == 0
                ? $"No running apps are locking the sampled files{suffix}."
                : $"Select apps below to close before running cleanup{suffix}.";
        }
        catch (OperationCanceledException)
        {
            // Selection changed quickly; ignore.
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Detecting locking apps failed: {ex.Message}");
            LockInspectionStatusMessage = "Unable to inspect running apps.";
            UpdateLockingProcesses(Array.Empty<ResourceLockInfo>());
        }
        finally
        {
            if (ReferenceEquals(_lockInspectionCancellation, cancellationSource))
            {
                cancellationSource.Dispose();
                _lockInspectionCancellation = null;
                IsLockInspectionInProgress = false;
                UpdateLockingProcessSummary();
            }
        }
    }

    private void CancelLockInspection()
    {
        var existing = Interlocked.Exchange(ref _lockInspectionCancellation, null);
        if (existing is null)
        {
            return;
        }

        try
        {
            existing.Cancel();
        }
        catch
        {
            // Suppress cancellation races.
        }
        finally
        {
            existing.Dispose();
        }

        IsLockInspectionInProgress = false;
    }

    [RelayCommand]
    private void RefreshLockingProcesses()
    {
        if (_pendingDeletionItems is null || _pendingDeletionItems.Count == 0)
        {
            _mainViewModel.SetStatusMessage("Select items to inspect before rescanning for locking apps.");
            return;
        }

        BeginLockInspection(_pendingDeletionItems);
    }

    [RelayCommand]
    private void SelectAllLockingProcesses()
    {
        foreach (var process in LockingProcesses)
        {
            process.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearLockingProcessSelections()
    {
        foreach (var process in LockingProcesses)
        {
            process.IsSelected = false;
        }
    }

    private bool CanCloseSelectedLockingProcesses() => !IsClosingLockingProcesses && LockingProcesses.Any(static vm => vm.IsSelected);

    [RelayCommand(CanExecute = nameof(CanCloseSelectedLockingProcesses))]
    private async Task CloseSelectedLockingProcessesAsync()
    {
        var targets = LockingProcesses.Where(static vm => vm.IsSelected).ToList();
        await CloseLockingProcessesAsync(targets, ResourceCloseMode.Graceful);
    }

    private bool CanForceCloseSelectedLockingProcesses() => CanCloseSelectedLockingProcesses();

    [RelayCommand(CanExecute = nameof(CanForceCloseSelectedLockingProcesses))]
    private async Task ForceCloseSelectedLockingProcessesAsync()
    {
        var targets = LockingProcesses.Where(static vm => vm.IsSelected).ToList();
        await CloseLockingProcessesAsync(targets, ResourceCloseMode.Force);
    }

    private bool CanCloseAllLockingProcesses() => !IsClosingLockingProcesses && HasLockingProcesses;

    [RelayCommand(CanExecute = nameof(CanCloseAllLockingProcesses))]
    private async Task CloseAllLockingProcessesAsync()
    {
        var targets = LockingProcesses.ToList();
        await CloseLockingProcessesAsync(targets, ResourceCloseMode.Graceful);
    }

    [RelayCommand]
    private void ShowLockingProcesses()
    {
        if (!IsLockingProcessPanelVisible)
        {
            return;
        }

        IsLockingProcessPopupOpen = true;
    }

    [RelayCommand]
    private void HideLockingProcesses()
    {
        IsLockingProcessPopupOpen = false;
    }

    private async Task CloseLockingProcessesAsync(IReadOnlyList<CleanupLockProcessViewModel> targets, ResourceCloseMode mode)
    {
        if (targets is null || targets.Count == 0)
        {
            _mainViewModel.SetStatusMessage("Select at least one app to close.");
            return;
        }

        IsClosingLockingProcesses = true;
        try
        {
            var handles = targets.Select(static vm => vm.Handle).ToList();
            var result = await _resourceLockService.CloseAsync(handles, mode);
            _mainViewModel.SetStatusMessage(result.Message);
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Closing apps failed: {ex.Message}");
        }
        finally
        {
            IsClosingLockingProcesses = false;
        }

        if (_pendingDeletionItems is not null && _pendingDeletionItems.Count > 0)
        {
            BeginLockInspection(_pendingDeletionItems);
        }
    }

    private void UpdateLockingProcesses(IReadOnlyList<ResourceLockInfo> processes)
    {
        foreach (var process in LockingProcesses)
        {
            process.PropertyChanged -= OnLockingProcessPropertyChanged;
        }

        LockingProcesses.Clear();
        for (var i = 0; i < processes.Count; i++)
        {
            LockingProcesses.Add(new CleanupLockProcessViewModel(processes[i]));
        }

        if (processes.Count == 0)
        {
            UpdateLockingProcessSummary();
        }
    }

    private void OnLockingProcessesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e is not null)
        {
            if (e.OldItems is not null)
            {
                foreach (CleanupLockProcessViewModel item in e.OldItems)
                {
                    item.PropertyChanged -= OnLockingProcessPropertyChanged;
                }
            }

            if (e.NewItems is not null)
            {
                foreach (CleanupLockProcessViewModel item in e.NewItems)
                {
                    item.PropertyChanged += OnLockingProcessPropertyChanged;
                }
            }
        }

        UpdateLockingProcessSummary();
        CloseSelectedLockingProcessesCommand.NotifyCanExecuteChanged();
        ForceCloseSelectedLockingProcessesCommand.NotifyCanExecuteChanged();
        CloseAllLockingProcessesCommand.NotifyCanExecuteChanged();
    }

    private void OnLockingProcessPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(CleanupLockProcessViewModel.IsSelected), StringComparison.Ordinal))
        {
            CloseSelectedLockingProcessesCommand.NotifyCanExecuteChanged();
            ForceCloseSelectedLockingProcessesCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateLockingProcessSummary()
    {
        OnPropertyChanged(nameof(HasLockingProcesses));
        OnPropertyChanged(nameof(IsLockingProcessPanelVisible));
        OnPropertyChanged(nameof(LockingProcessSummary));
        OnPropertyChanged(nameof(LockingProcessButtonLabel));
    }

    private sealed record LockInspectionSample(IReadOnlyList<string> Paths, LockInspectionSampleStats Stats);

    private readonly record struct LockInspectionSampleStats(int TotalItems, int SampledItems, double CoverageFraction)
    {
        public bool HasCoverage => CoverageFraction > 0;
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
        bool generateReport,
        bool showCelebration = true)
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

        var manualBrowserEntries = new List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item, CleanupDeletionEntry entry)>();
        BrowserHistoryHandleResult browserHistoryResult = BrowserHistoryHandleResult.Empty;

        try
        {
            IsDeleting = true;
            IsBusy = true;

            if (_browserCleanupService is not null)
            {
                browserHistoryResult = await HandleEdgeHistoryAsync(itemsToDelete, CancellationToken.None);
                manualBrowserEntries.AddRange(browserHistoryResult.Entries);
            }

            var handledItems = new HashSet<CleanupPreviewItemViewModel>(manualBrowserEntries.Select(static entry => entry.item));
            var models = itemsToDelete
                .Where(tuple => !handledItems.Contains(tuple.item))
                .Select(static tuple => tuple.item.Model)
                .ToList();

            if (models.Count == 0 && manualBrowserEntries.Count == 0)
            {
                _mainViewModel.SetStatusMessage("Cleanup cancelled — nothing remains selected.");
                return;
            }

            DeletionProgressTotal = models.Count;
            DeletionProgressCurrent = 0;
            DeletionStatusMessage = totalSizeMb > 0
                ? $"Removing {itemsToDelete.Count:N0} item(s) • {totalSizeMb:F2} MB"
                : $"Removing {itemsToDelete.Count:N0} item(s)";

            var progressStopwatch = Stopwatch.StartNew();
            var lastProgressReported = -1;

            var progress = new Progress<CleanupDeletionProgress>(report =>
            {
                if (report.Total > 0 && DeletionProgressTotal != report.Total)
                {
                    DeletionProgressTotal = report.Total;
                }

                var shouldRefresh =
                    report.Completed == report.Total ||
                    report.Completed == 0 ||
                    report.Completed - lastProgressReported >= 25 ||
                    progressStopwatch.Elapsed >= TimeSpan.FromMilliseconds(120);

                if (!shouldRefresh)
                {
                    return;
                }

                lastProgressReported = report.Completed;
                progressStopwatch.Restart();

                DeletionProgressCurrent = report.Completed;
                if (string.IsNullOrEmpty(report.CurrentPath))
                {
                    DeletionStatusMessage = report.Total > 0
                        ? $"Deleting {report.Completed} of {report.Total}"
                        : "Deleting…";
                }
                else
                {
                    DeletionStatusMessage = $"Deleting {report.Completed}/{report.Total}: {report.CurrentPath}";
                }
            });

            var stopwatch = Stopwatch.StartNew();
            var deletionResult = await _cleanupService.DeleteAsync(models, progress, deletionOptions);
            stopwatch.Stop();

            var combinedResult = manualBrowserEntries.Count == 0
                ? deletionResult
                : new CleanupDeletionResult(manualBrowserEntries.Select(static tuple => tuple.entry).Concat(deletionResult.Entries));

            var entryLookup = await Task.Run(() => BuildDeletionEntryLookup(combinedResult), CancellationToken.None);

            var removalCandidates = new List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)>();
            var categoriesTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failureItems = new List<CleanupCelebrationFailureViewModel>();
            var processedItems = 0;

            foreach (var tuple in itemsToDelete)
            {
                if (++processedItems % DeletionUiYieldInterval == 0)
                {
                    await Task.Yield();
                }

                var path = tuple.item.Model.FullName;
                if (!entryLookup.TryGetValue(path, out var entry))
                {
                    removalCandidates.Add(tuple);
                    if (!string.IsNullOrWhiteSpace(tuple.group.Category))
                    {
                        categoriesTouched.Add(tuple.group.Category);
                    }

                    continue;
                }

                switch (entry.Disposition)
                {
                    case CleanupDeletionDisposition.Deleted:
                        removalCandidates.Add(tuple);
                        if (!string.IsNullOrWhiteSpace(tuple.group.Category))
                        {
                            categoriesTouched.Add(tuple.group.Category);
                        }

                        break;

                    case CleanupDeletionDisposition.Skipped:
                        if (ShouldKeepSkippedEntry(entry))
                        {
                            tuple.item.IsSelected = false;
                            failureItems.Add(new CleanupCelebrationFailureViewModel(tuple.group, tuple.item, entry));
                            if (!string.IsNullOrWhiteSpace(tuple.group.Category))
                            {
                                categoriesTouched.Add(tuple.group.Category);
                            }
                        }
                        else
                        {
                            removalCandidates.Add(tuple);
                            if (!string.IsNullOrWhiteSpace(tuple.group.Category))
                            {
                                categoriesTouched.Add(tuple.group.Category);
                            }
                        }

                        break;

                    case CleanupDeletionDisposition.Failed:
                        tuple.item.IsSelected = false;
                        failureItems.Add(new CleanupCelebrationFailureViewModel(tuple.group, tuple.item, entry));
                        if (!string.IsNullOrWhiteSpace(tuple.group.Category))
                        {
                            categoriesTouched.Add(tuple.group.Category);
                        }
                        break;
                }
            }

            if (removalCandidates.Count > 0)
            {
                var removalByGroup = new Dictionary<CleanupTargetGroupViewModel, List<CleanupPreviewItemViewModel>>();
                foreach (var (group, item) in removalCandidates)
                {
                    if (!removalByGroup.TryGetValue(group, out var list))
                    {
                        list = new List<CleanupPreviewItemViewModel>();
                        removalByGroup[group] = list;
                    }

                    list.Add(item);
                }

                var processedGroups = 0;
                foreach (var kvp in removalByGroup)
                {
                    kvp.Key.RemoveItems(kvp.Value);
                    processedGroups++;

                    if (processedGroups % 3 == 0)
                    {
                        await Task.Yield();
                    }
                }
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

            var deletionSummary = combinedResult.ToStatusMessage();
            if (browserHistoryResult.Warnings.Count > 0)
            {
                deletionSummary += " • Edge cleanup: " + string.Join(", ", browserHistoryResult.Warnings);
            }
            string? reportPath = null;
            string? reportError = null;
            if (generateReport)
            {
                reportPath = TryGenerateCleanupReport(combinedResult, out reportError);
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

            if (combinedResult.HasErrors && combinedResult.Errors.Count > 0)
            {
                DeletionStatusMessage = deletionSummary + " • " + combinedResult.Errors[0];
            }
            else
            {
                DeletionStatusMessage = deletionSummary;
            }

            if (showCelebration)
            {
                await ShowCleanupCelebrationAsync(
                    combinedResult,
                    categoriesTouched,
                    failureItems,
                    stopwatch.Elapsed,
                    reportPath,
                    deletionSummary);
            }
            else
            {
                CelebrationFailures.Clear();
                foreach (var failure in failureItems)
                {
                    CelebrationFailures.Add(failure);
                }
            }
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Delete failed: {ex.Message}");
            DeletionStatusMessage = ex.Message;
        }
        finally
        {
            BusyStatusMessage = "Working…";
            BusyStatusDetail = string.Empty;
            IsDeleting = false;
            IsBusy = false;
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            ConfirmCleanupCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectionSummaryText));
            OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        }
    }

    private async Task<BrowserHistoryHandleResult> HandleEdgeHistoryAsync(
        IReadOnlyList<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> items,
        CancellationToken cancellationToken)
    {
        if (items is null || items.Count == 0)
        {
            return BrowserHistoryHandleResult.Empty;
        }

        var grouped = new Dictionary<string, List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tuple in items)
        {
            if (tuple.item is null || !string.Equals(tuple.item.Classification, "History", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidatePath = tuple.item.Model.FullName;
            if (!BrowserHistoryHelper.TryGetEdgeProfileDirectory(candidatePath, out var profileDirectory))
            {
                continue;
            }

            if (!grouped.TryGetValue(profileDirectory, out var list))
            {
                list = new List<(CleanupTargetGroupViewModel, CleanupPreviewItemViewModel)>();
                grouped[profileDirectory] = list;
            }

            list.Add(tuple);
        }

        if (grouped.Count == 0)
        {
            return BrowserHistoryHandleResult.Empty;
        }

        var entries = new List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item, CleanupDeletionEntry entry)>();
        var warnings = new List<string>();

        foreach (var kvp in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _browserCleanupService.ClearEdgeHistoryAsync(kvp.Key, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                foreach (var tuple in kvp.Value)
                {
                    var entry = new CleanupDeletionEntry(
                        tuple.item.Model.FullName,
                        tuple.item.Model.SizeBytes,
                        tuple.item.IsDirectory,
                        CleanupDeletionDisposition.Deleted,
                        "Cleared safely via Microsoft Edge API.");
                    entries.Add((tuple.group, tuple.item, entry));
                }
            }
            else
            {
                warnings.Add(result.Message);
            }
        }

        return entries.Count == 0 && warnings.Count == 0
            ? BrowserHistoryHandleResult.Empty
            : new BrowserHistoryHandleResult(entries, warnings);
    }

    private sealed record BrowserHistoryHandleResult(
        IReadOnlyList<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item, CleanupDeletionEntry entry)> Entries,
        IReadOnlyList<string> Warnings)
    {
        public static BrowserHistoryHandleResult Empty { get; } = new(
            Array.Empty<(CleanupTargetGroupViewModel, CleanupPreviewItemViewModel, CleanupDeletionEntry)>(),
            Array.Empty<string>());
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

    private async Task ShowCleanupCelebrationAsync(
        CleanupDeletionResult deletionResult,
        IReadOnlyCollection<string> categoriesTouched,
        IReadOnlyList<CleanupCelebrationFailureViewModel> failureItems,
        TimeSpan executionDuration,
        string? reportPath,
        string deletionSummary)
    {
        var reclaimedMegabytes = deletionResult.TotalBytesDeleted / 1_048_576d;
        CelebrationItemsDeleted = deletionResult.DeletedCount;
        CelebrationItemsSkipped = deletionResult.SkippedCount;
        CelebrationItemsFailed = deletionResult.FailedCount;
        CelebrationReclaimedMegabytes = reclaimedMegabytes;
        var normalizedCategories = NormalizeDistinctCategories(categoriesTouched);
        CelebrationCategoryCount = normalizedCategories.Count;
        UpdateCategoryCollection(_celebrationCategories, normalizedCategories);
        CelebrationCategoryList = BuildCategoryListText(normalizedCategories);

        CelebrationHeadline = reclaimedMegabytes > 0
            ? $"Cleanup complete — {FormatSize(reclaimedMegabytes)} reclaimed"
            : deletionResult.DeletedCount > 0
                ? "Cleanup complete"
                : "No items removed";

        CelebrationDetails = BuildCelebrationDetails(deletionResult, CelebrationCategoryCount, reclaimedMegabytes);
        CelebrationDurationDisplay = FormatDuration(executionDuration);
        var estimatedTimeSaved = EstimateTimeSaved(deletionResult.DeletedCount, deletionResult.TotalBytesDeleted, executionDuration);
        CelebrationTimeSavedDisplay = FormatDuration(estimatedTimeSaved);

        CelebrationFailures.Clear();
        foreach (var failure in failureItems)
        {
            CelebrationFailures.Add(failure);
        }

        CelebrationReportPath = !string.IsNullOrWhiteSpace(reportPath) && File.Exists(reportPath)
            ? reportPath
            : null;

        var shareCategories = CelebrationCategories.Count == 0
            ? CelebrationCategoryListDisplay
            : string.Join(", ", CelebrationCategories);
        CelebrationShareSummary = BuildCelebrationShareSummary(
            CelebrationHeadline,
            deletionResult,
            CelebrationCategoryCount,
            shareCategories,
            CelebrationTimeSavedDisplay,
            CelebrationReportPath);

        if (deletionResult.DeletedCount == 0 && deletionResult.TotalBytesDeleted == 0 && failureItems.Count > 0)
        {
            DeletionStatusMessage = deletionSummary;
        }

        if (IsConfirmationSheetVisible)
        {
            IsConfirmationSheetVisible = false;
        }

        await TransitionToPhaseAsync(
            CleanupPhase.Celebration,
            transitionMessage: "Finalizing cleanup summary…",
            preTransitionDelay: TimeSpan.FromMilliseconds(140),
            settleDelay: TimeSpan.FromMilliseconds(260));
    }

    private static bool ShouldKeepSkippedEntry(CleanupDeletionEntry entry)
    {
        if (entry is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.Reason))
        {
            return true;
        }

        return entry.Reason.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static Dictionary<string, CleanupDeletionEntry> BuildDeletionEntryLookup(CleanupDeletionResult result)
    {
        var lookup = new Dictionary<string, CleanupDeletionEntry>(StringComparer.OrdinalIgnoreCase);

        if (result?.Entries is null || result.Entries.Count == 0)
        {
            return lookup;
        }

        foreach (var entry in result.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            lookup[entry.Path] = entry;
        }

        return lookup;
    }

    private static string BuildCategoryListText(IEnumerable<string>? categories)
    {
        if (categories is null)
        {
            return string.Empty;
        }

        var normalized = categories as IReadOnlyList<string> ?? NormalizeDistinctCategories(categories);

        if (normalized.Count == 0)
        {
            return string.Empty;
        }

        if (normalized.Count <= 4)
        {
            return string.Join(", ", normalized);
        }

        return string.Join(", ", normalized.Take(4)) + ", ...";
    }

    private static IReadOnlyList<string> NormalizeDistinctCategories(IEnumerable<string>? categories)
    {
        if (categories is null)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            if (seen.Add(category))
            {
                normalized.Add(category);
            }
        }

        return normalized;
    }

    private static void UpdateCategoryCollection(ObservableCollection<string> target, IReadOnlyList<string> categories)
    {
        target.Clear();
        for (var i = 0; i < categories.Count; i++)
        {
            target.Add(categories[i]);
        }
    }

    private static string BuildCelebrationDetails(CleanupDeletionResult result, int categoryCount, double reclaimedMegabytes)
    {
        var parts = new List<string>();

        if (result.DeletedCount > 0)
        {
            parts.Add($"{result.DeletedCount:N0} item(s) removed");
        }

        if (categoryCount > 0)
        {
            parts.Add($"{categoryCount:N0} categories affected");
        }

        if (reclaimedMegabytes > 0)
        {
            parts.Add($"{FormatSize(reclaimedMegabytes)} reclaimed");
        }

        if (result.SkippedCount > 0)
        {
            parts.Add($"{result.SkippedCount:N0} skipped");
        }

        if (result.FailedCount > 0)
        {
            parts.Add($"{result.FailedCount:N0} failed");
        }

        return parts.Count == 0 ? "No files required cleanup." : string.Join(" • ", parts);
    }

    private static string BuildCelebrationShareSummary(
        string headline,
        CleanupDeletionResult result,
        int categoryCount,
        string categoriesDisplay,
        string timeSavedDisplay,
        string? reportPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine(headline);
        builder.AppendLine($"Items removed: {result.DeletedCount:N0}");
        builder.AppendLine($"Space reclaimed: {FormatSize(result.TotalBytesDeleted / 1_048_576d)}");
        if (categoryCount > 0 && !string.IsNullOrWhiteSpace(categoriesDisplay) && categoriesDisplay != "—")
        {
            builder.AppendLine($"Categories touched: {categoriesDisplay}");
        }

        builder.AppendLine($"Estimated time saved: {timeSavedDisplay}");

        if (result.FailedCount > 0)
        {
            builder.AppendLine($"Attention needed: {result.FailedCount:N0} item(s) require follow-up.");
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            builder.AppendLine($"Detailed report: {reportPath}");
        }

        return builder.ToString();
    }

    private static TimeSpan EstimateTimeSaved(int deletedCount, long totalBytesDeleted, TimeSpan executionDuration)
    {
        if (deletedCount <= 0 && totalBytesDeleted <= 0)
        {
            return executionDuration;
        }

        var perItemSeconds = Math.Max(0, deletedCount) * 6d;
        var sizeBonusSeconds = Math.Max(0d, totalBytesDeleted / 1_073_741_824d) * 60d;
        var estimateSeconds = Math.Max(30d, perItemSeconds + sizeBonusSeconds);

        var estimate = TimeSpan.FromSeconds(estimateSeconds);
        if (estimate < executionDuration + TimeSpan.FromSeconds(15))
        {
            estimate = executionDuration + TimeSpan.FromSeconds(15);
        }

        return estimate;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:F1} hr";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.TotalMinutes:F1} min";
        }

        return $"{duration.TotalSeconds:F0} sec";
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllCurrent))]
    private void SelectAllCurrent()
    {
        ApplySelectionAcrossTargets(true);
    }

    private bool CanSelectAllCurrent()
    {
        return HasFilteredItemsAcrossTargets();
    }

    [RelayCommand(CanExecute = nameof(CanSelectAcrossPages))]
    private void SelectAllPages()
    {
        ApplySelectionToCurrentTarget(true);
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
        if (_previewPagingController.TotalFilteredItems == 0 || totalPages <= 0)
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
            foreach (var item in target.Items.Where(_previewFilter.Matches))
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
        return SelectedTarget is not null && _previewPagingController.TotalFilteredItems > 0;
    }

    [RelayCommand(CanExecute = nameof(CanClearCurrentSelection))]
    private void ClearCurrentSelection()
    {
        ApplySelectionAcrossTargets(false);
    }

    private bool CanClearCurrentSelection() => HasSelection;

    private void ApplySelectionAcrossTargets(bool isSelected)
    {
        if (Targets.Count == 0)
        {
            return;
        }

        foreach (var group in Targets)
        {
            ApplySelectionToGroup(group, isSelected);
        }
    }

    private void ApplySelectionToCurrentTarget(bool isSelected, bool currentPageOnly = false)
    {
        var target = SelectedTarget;
        if (target is null)
        {
            return;
        }

        ApplySelectionToGroup(target, isSelected, currentPageOnly);
    }

    private void ApplySelectionToGroup(CleanupTargetGroupViewModel group, bool isSelected, bool currentPageOnly = false)
    {
        IEnumerable<CleanupPreviewItemViewModel> items;

        if (currentPageOnly)
        {
            if (!ReferenceEquals(group, SelectedTarget))
            {
                return;
            }

            items = FilteredItems;
        }
        else
        {
            items = group.Items.Where(_previewFilter.Matches);
        }

        using (group.BeginSelectionUpdate())
        {
            foreach (var item in items)
            {
                item.IsSelected = isSelected;
            }
        }
    }

    private bool HasFilteredItemsAcrossTargets()
    {
        foreach (var group in Targets)
        {
            if (group.Items.Any(_previewFilter.Matches))
            {
                return true;
            }
        }

        return false;
    }

    [RelayCommand]
    private void ResetFilters()
    {
        IncludeDownloads = true;
        IncludeBrowserHistory = true;
        SelectedItemKind = CleanupItemKind.Both;
        SelectedExtensionFilterMode = CleanupExtensionFilterMode.None;
        SelectedExtensionProfile = ExtensionProfiles.FirstOrDefault();
        CustomExtensionInput = string.Empty;
        PreviewCount = DefaultPreviewCount;
        SelectRangeStartPage = 1;
        SelectRangeEndPage = 1;
        PreviewSortMode = CleanupPreviewSortMode.Impact;
        _mainViewModel.SetStatusMessage("Cleanup filters reset to defaults.");
        ResetCurrentPage();
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

    [RelayCommand]
    private async Task CloseCelebrationAsync()
    {
        CelebrationFailures.Clear();
        var nextPhase = Targets.Count > 0 ? CleanupPhase.Preview : CleanupPhase.Setup;
        var transitionMessage = nextPhase == CleanupPhase.Preview
            ? "Reopening preview…"
            : "Resetting cleanup flow…";

        await TransitionToPhaseAsync(nextPhase, transitionMessage);
        _mainViewModel.SetStatusMessage("Cleanup summary dismissed. Ready for the next scan.");
    }

    [RelayCommand]
    private void ShareCelebrationSummary()
    {
        if (string.IsNullOrWhiteSpace(CelebrationShareSummary))
        {
            _mainViewModel.SetStatusMessage("Cleanup summary is not available yet.");
            return;
        }

        try
        {
            WindowsClipboard.SetText(CelebrationShareSummary);
            _mainViewModel.SetStatusMessage("Cleanup summary copied to clipboard.");
        }
        catch
        {
            _mainViewModel.SetStatusMessage("Unable to access clipboard for sharing.");
        }
    }

    [RelayCommand]
    private void ReviewCelebrationReport()
    {
        var path = CelebrationReportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _mainViewModel.SetStatusMessage("Cleanup report is not available.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
            _mainViewModel.SetStatusMessage("Opening cleanup report...");
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Unable to open report: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ScheduleRecurringScan()
    {
        _mainViewModel.SetStatusMessage("Recurring cleanup scheduling will arrive soon. In the meantime, set reminders from Settings when available.");
    }

    [RelayCommand]
    private void DismissRefreshToast()
    {
        HideRefreshToast();
    }

    [RelayCommand]
    private async Task RetryFailureAsync(CleanupCelebrationFailureViewModel? failure)
    {
        if (failure is null || IsBusy)
        {
            return;
        }

        if (!failure.Group.Items.Contains(failure.Item))
        {
            CelebrationFailures.Remove(failure);
            _mainViewModel.SetStatusMessage("Item already removed — nothing left to retry.");
            return;
        }

        try
        {
            failure.IsRetrying = true;
            await ExecuteDeletionAsync(
                new[] { (failure.Group, failure.Item) },
                new CleanupDeletionOptions
                {
                    PreferRecycleBin = UseRecycleBin,
                    AllowPermanentDeleteFallback = true
                },
                generateReport: false);
        }
        finally
        {
            failure.IsRetrying = false;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
    }

    partial void OnBusyStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(ActiveOperationStatus));
        OnPropertyChanged(nameof(ActiveOperationDetail));
    }

    partial void OnBusyStatusDetailChanged(string value)
    {
        OnPropertyChanged(nameof(ActiveOperationDetail));
    }

    partial void OnIsDeletingChanged(bool value)
    {
        OnPropertyChanged(nameof(ActiveOperationStatus));
        OnPropertyChanged(nameof(ActiveOperationDetail));
        OnPropertyChanged(nameof(ActiveOperationProgressValue));
        OnPropertyChanged(nameof(ActiveOperationProgressMaximum));
        OnPropertyChanged(nameof(IsActiveOperationIndeterminate));
        OnPropertyChanged(nameof(ActiveOperationPercentDisplay));
        OnPropertyChanged(nameof(HasActiveOperationPercent));
    }

    partial void OnDeletionStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(ActiveOperationStatus));
        OnPropertyChanged(nameof(ActiveOperationDetail));
    }

    partial void OnDeletionProgressCurrentChanged(int value)
    {
        OnPropertyChanged(nameof(ActiveOperationDetail));
        OnPropertyChanged(nameof(ActiveOperationProgressValue));
        OnPropertyChanged(nameof(IsActiveOperationIndeterminate));
        OnPropertyChanged(nameof(ActiveOperationPercentDisplay));
        OnPropertyChanged(nameof(HasActiveOperationPercent));
    }

    partial void OnDeletionProgressTotalChanged(int value)
    {
        OnPropertyChanged(nameof(ActiveOperationDetail));
        OnPropertyChanged(nameof(ActiveOperationProgressMaximum));
        OnPropertyChanged(nameof(IsActiveOperationIndeterminate));
        OnPropertyChanged(nameof(ActiveOperationPercentDisplay));
        OnPropertyChanged(nameof(HasActiveOperationPercent));
    }

    partial void OnSelectedTargetChanged(CleanupTargetGroupViewModel? oldValue, CleanupTargetGroupViewModel? newValue)
    {
        if (newValue is null)
        {
            ResetCurrentPage();
            RefreshFilteredItems();
            SelectAllCurrentCommand.NotifyCanExecuteChanged();
            ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
            SelectAllPagesCommand.NotifyCanExecuteChanged();
            SelectPageRangeCommand.NotifyCanExecuteChanged();
            return;
        }

        ResetCurrentPage();
        RefreshFilteredItems();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        SelectAllPagesCommand.NotifyCanExecuteChanged();
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedItemKindChanged(CleanupItemKind value)
    {
        _previewFilter.SelectedItemKind = value;
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

    partial void OnCelebrationReclaimedMegabytesChanged(double value)
    {
        OnPropertyChanged(nameof(CelebrationReclaimedDisplay));
    }

    partial void OnCelebrationCategoryListChanged(string value)
    {
        OnPropertyChanged(nameof(CelebrationCategoryListDisplay));
    }

    partial void OnCelebrationReportPathChanged(string? value)
    {
        OnPropertyChanged(nameof(CanReviewCelebrationReport));
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
        OnPropertyChanged(nameof(IsCelebrationPhase));
        if (value == CleanupPhase.Setup)
        {
            HideRefreshToast();
        }
    }

    partial void OnSelectedExtensionFilterModeChanged(CleanupExtensionFilterMode value)
    {
        _previewFilter.ExtensionFilterMode = value;
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

    partial void OnSelectedAgeFilterChanged(CleanupAgeFilterOption? value)
    {
        _minimumAgeThresholdUtc = value is null || value.Days <= 0
            ? null
            : DateTime.UtcNow - TimeSpan.FromDays(value.Days);
        _previewFilter.MinimumAgeThresholdUtc = _minimumAgeThresholdUtc;
        RefreshFilteredItems();
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

    private void OnCelebrationFailuresCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasCelebrationFailures));
    }

    private void OnPendingDeletionCategoriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingDeletionCategories));
    }

    private void OnCelebrationCategoriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasCelebrationCategories));
    }

    private void HandleRefreshToast(int newItems, int totalItems)
    {
        if (_hasCompletedPreview)
        {
            if (newItems > 0)
            {
                var message = newItems == 1
                    ? "1 new item surfaced since your last preview."
                    : $"{newItems:N0} new items surfaced since your last preview.";
                ShowRefreshToast(message);
            }
            else
            {
                HideRefreshToast();
            }
        }

        _hasCompletedPreview = true;

        if (totalItems == 0)
        {
            HideRefreshToast();
        }
    }

    private void ShowRefreshToast(string message, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        RefreshToastText = message;
        IsRefreshToastVisible = true;

        _refreshToastCancellation?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshToastCancellation = cts;

        var delay = duration ?? TimeSpan.FromSeconds(5);
        _ = DismissRefreshToastAfterDelayAsync(delay, cts.Token);
    }

    private void HideRefreshToast()
    {
        _refreshToastCancellation?.Cancel();
        _refreshToastCancellation = null;

        if (IsRefreshToastVisible)
        {
            IsRefreshToastVisible = false;
        }
    }

    private async Task TransitionToPhaseAsync(
        CleanupPhase targetPhase,
        string? transitionMessage = null,
        TimeSpan? preTransitionDelay = null,
        TimeSpan? settleDelay = null)
    {
        if (CurrentPhase == targetPhase && !IsPhaseTransitioning)
        {
            return;
        }

        var previous = _phaseTransitionCancellation;
        var cts = new CancellationTokenSource();
        _phaseTransitionCancellation = cts;
        previous?.Cancel();

        PhaseTransitionMessage = string.IsNullOrWhiteSpace(transitionMessage)
            ? BuildDefaultPhaseTransitionMessage(targetPhase)
            : transitionMessage;
        IsPhaseTransitioning = true;

        try
        {
            var lead = preTransitionDelay ?? _phaseTransitionLeadDuration;
            if (lead > TimeSpan.Zero)
            {
                await Task.Delay(lead, cts.Token);
            }

            if (_phaseTransitionCancellation != cts)
            {
                return;
            }

            if (CurrentPhase != targetPhase)
            {
                CurrentPhase = targetPhase;
            }

            var settle = settleDelay ?? _phaseTransitionSettleDuration;
            if (settle > TimeSpan.Zero)
            {
                await Task.Delay(settle, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer transition superseded this one; the latest will tidy up state.
        }
        finally
        {
            if (ReferenceEquals(_phaseTransitionCancellation, cts))
            {
                _phaseTransitionCancellation = null;
                PhaseTransitionMessage = string.Empty;
                IsPhaseTransitioning = false;
            }

            cts.Dispose();
            previous?.Dispose();
        }
    }

    private static string BuildDefaultPhaseTransitionMessage(CleanupPhase targetPhase) => targetPhase switch
    {
        CleanupPhase.Setup => "Preparing setup tools…",
        CleanupPhase.Preview => "Loading preview results…",
        CleanupPhase.Celebration => "Summarizing cleanup…",
        _ => "Preparing next phase…"
    };

    private async Task DismissRefreshToastAfterDelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() =>
            {
                IsRefreshToastVisible = false;
                RefreshToastText = string.Empty;
            });
        }
        else
        {
            IsRefreshToastVisible = false;
            RefreshToastText = string.Empty;
        }
    }

    private void ClearTargets()
    {
        foreach (var group in Targets.ToList())
        {
            RemoveTargetGroup(group);
        }

        SelectedTarget = null;
        _previewPagingController.Reset();
        HideRefreshToast();
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
        _previewPagingController.Refresh();
    }

    private void OnPreviewPagingStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageSize));
        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(SelectRangeStartPage));
        OnPropertyChanged(nameof(SelectRangeEndPage));
        OnPropertyChanged(nameof(PreviewSortMode));
        OnPropertyChanged(nameof(HasFilteredResults));
        OnPropertyChanged(nameof(FilteredItems));
        OnPropertyChanged(nameof(ExtensionStatusText));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        SelectAllPagesCommand.NotifyCanExecuteChanged();
        SelectPageRangeCommand.NotifyCanExecuteChanged();
    }

    private void RebuildExtensionCache()
    {
        _activeExtensions.Clear();

        if (SelectedExtensionFilterMode != CleanupExtensionFilterMode.None)
        {
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

        _previewFilter.SetActiveExtensions(_activeExtensions);
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

    private IEnumerable<CleanupTargetReport> FilterPreviewTargets(IEnumerable<CleanupTargetReport> targets)
    {
        if (targets is null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (target is null)
            {
                continue;
            }

            if (target.ItemCount <= 0 || target.Preview.Count == 0)
            {
                continue;
            }

            var key = BuildTargetIdentity(target);
            if (!seen.Add(key))
            {
                continue;
            }

            yield return target;
        }
    }

    private static string BuildTargetIdentity(CleanupTargetReport target)
    {
        if (!string.IsNullOrWhiteSpace(target.Path))
        {
            return target.Path.Trim();
        }

        var category = target.Category?.Trim() ?? string.Empty;
        var classification = target.Classification?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(category) || !string.IsNullOrEmpty(classification))
        {
            return string.IsNullOrEmpty(classification)
                ? category
                : $"{category}|{classification}";
        }

        return Guid.NewGuid().ToString("N");
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
        return CleanupPreviewFilter.NormalizeExtension(extension);
    }
}
