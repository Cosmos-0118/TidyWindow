using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels.Filters;
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

public sealed record CleanupAutomationIntervalOption(int Minutes, string Label, string Description)
{
    public override string ToString() => Label;
}

internal sealed record PreviewMaterialization(
    IReadOnlyList<CleanupTargetReport> Targets,
    int TotalItems,
    int NewItems);

public sealed record CleanupAutomationDeletionModeOption(CleanupAutomationDeletionMode Mode, string Label, string Description)
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

public sealed partial class CleanupViewModel : ViewModelBase, IDisposable
{
    private readonly CleanupService _cleanupService;
    private readonly MainViewModel _mainViewModel;
    private readonly IPrivilegeService _privilegeService;
    private readonly IResourceLockService _resourceLockService;
    private readonly IBrowserCleanupService _browserCleanupService;
    private readonly CleanupAutomationScheduler _cleanupAutomationScheduler;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly IRelativeTimeTicker _relativeTimeTicker;

    private const int PreviewCountMinimumValue = 10;
    private const int PreviewCountMaximumValue = 100_000;
    private const int DefaultPreviewCount = 50;
    private const int MaxLockInspectionItemsPerCategory = 32;
    private const int MaxLockInspectionSampleTotal = 600;
    private const int PreviewUiYieldInterval = 3;

    private readonly CleanupPreviewFilter _previewFilter;
    private readonly PreviewPagingController _previewPagingController;
    private readonly CleanupExtensionFilterModel _extensionFilterModel;
    private int _previewCount = DefaultPreviewCount;
    private List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)>? _pendingDeletionItems;
    private int _lastPreviewTotalItemCount;
    private bool _hasCompletedPreview;
    private CancellationTokenSource? _refreshToastCancellation;
    private CancellationTokenSource? _phaseTransitionCancellation;
    private CancellationTokenSource? _lockInspectionCancellation;
    private readonly TimeSpan _phaseTransitionLeadDuration = TimeSpan.FromMilliseconds(160);
    private readonly TimeSpan _phaseTransitionSettleDuration = TimeSpan.FromMilliseconds(220);
    private const int DeletionUiYieldInterval = 200;
    private LockInspectionSampleStats _lastLockInspectionStats = new(0, 0, 0);
    private DateTime? _minimumAgeThresholdUtc;
    private bool _suspendAutomationStateUpdates;
    private bool _disposed;

    public CleanupViewModel(
        CleanupService cleanupService,
        MainViewModel mainViewModel,
        IPrivilegeService privilegeService,
        IResourceLockService resourceLockService,
        IBrowserCleanupService browserCleanupService,
        CleanupAutomationScheduler cleanupAutomationScheduler,
        IAutomationWorkTracker workTracker,
        IRelativeTimeTicker relativeTimeTicker)
    {
        _cleanupService = cleanupService;
        _mainViewModel = mainViewModel;
        _privilegeService = privilegeService;
        _resourceLockService = resourceLockService;
        _browserCleanupService = browserCleanupService;
        _cleanupAutomationScheduler = cleanupAutomationScheduler ?? throw new ArgumentNullException(nameof(cleanupAutomationScheduler));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));
        _relativeTimeTicker = relativeTimeTicker ?? throw new ArgumentNullException(nameof(relativeTimeTicker));

        _previewFilter = new CleanupPreviewFilter
        {
            SelectedItemKind = _selectedItemKind,
            ExtensionFilterMode = CleanupExtensionFilterMode.None,
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

        var extensionFilterOptions = new List<CleanupExtensionFilterOption>
        {
            new(CleanupExtensionFilterMode.None, "No extension filter", "Show every item regardless of extension."),
            new(CleanupExtensionFilterMode.IncludeOnly, "Include only", "Keep the extensions listed below."),
            new(CleanupExtensionFilterMode.Exclude, "Exclude", "Hide the extensions listed below.")
        };

        var extensionProfiles = new List<CleanupExtensionProfile>
        {
            new("Documents", "Common document formats", new[] { ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".txt", ".rtf" }),
            new("Spreadsheets", "Spreadsheet data", new[] { ".xls", ".xlsx", ".ods", ".csv" }),
            new("Images", "Photo and bitmap formats", new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff" }),
            new("Media", "Audio and video clips", new[] { ".mp3", ".wav", ".mp4", ".mov", ".mkv" }),
            new("Archives", "Compressed archives", new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }),
            new("Logs", "Plain-text logs", new[] { ".log" })
        };

        _extensionFilterModel = new CleanupExtensionFilterModel(extensionFilterOptions, extensionProfiles);
        _extensionFilterModel.PropertyChanged += OnExtensionFilterPropertyChanged;
        _extensionFilterModel.FilterChanged += OnExtensionFilterChanged;
        _previewFilter.ExtensionFilterMode = _extensionFilterModel.Mode;
        _previewFilter.SetActiveExtensions(_extensionFilterModel.ActiveExtensions);

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
        CelebrationFailures.CollectionChanged += OnCelebrationFailuresCollectionChanged;
        PendingDeletionCategories = new ReadOnlyObservableCollection<string>(_pendingDeletionCategories);
        CelebrationCategories = new ReadOnlyObservableCollection<string>(_celebrationCategories);
        _pendingDeletionCategories.CollectionChanged += OnPendingDeletionCategoriesCollectionChanged;
        _celebrationCategories.CollectionChanged += OnCelebrationCategoriesCollectionChanged;
        LockingProcesses.CollectionChanged += OnLockingProcessesCollectionChanged;

        AutomationIntervalOptions = new List<CleanupAutomationIntervalOption>
        {
            new(60, "Every 1 hour", "Lightweight hourly sweep"),
            new(360, "Every 6 hours", "Checks in a few times a day"),
            new(1_440, "Every day", "Balanced once-daily cleanup"),
            new(10_080, "Every week", "Weekend cleanup run"),
            new(43_200, "Every month", "Monthly deep tidy")
        };

        AutomationDeletionModeOptions = new List<CleanupAutomationDeletionModeOption>
        {
            new(CleanupAutomationDeletionMode.SkipLocked, "Skip locked items", "Avoids files currently in use."),
            new(CleanupAutomationDeletionMode.MoveToRecycleBin, "Move to dustbin", "Send items to Recycle Bin first."),
            new(CleanupAutomationDeletionMode.ForceDelete, "Force delete", "Take ownership and remove stubborn files.")
        };

        _cleanupAutomationScheduler.SettingsChanged += OnCleanupAutomationSettingsChanged;
        _relativeTimeTicker.Tick += OnRelativeTimeTick;
        ApplyAutomationSettingsSnapshot(_cleanupAutomationScheduler.CurrentSettings);
    }

    [ObservableProperty]
    private bool _includeDownloads = false;

    [ObservableProperty]
    private bool _includeBrowserHistory = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDeletionPreparationInProgress;

    [ObservableProperty]
    private bool _isPreviewConfirmationPopupOpen;

    [ObservableProperty]
    private bool _isPreviewScanInProgress;

    [ObservableProperty]
    private bool _isCleanupExecutionInProgress;

    [ObservableProperty]
    private bool _isCelebrationErrorsPopupOpen;

    [ObservableProperty]
    private string _headline = "Preview and clean up system clutter";

    [ObservableProperty]
    private CleanupTargetGroupViewModel? _selectedTarget;

    [ObservableProperty]
    private CleanupItemKind _selectedItemKind = CleanupItemKind.Both;

    [ObservableProperty]
    private CleanupAgeFilterOption? _selectedAgeFilter;

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
    private int _scanProgressCurrent;

    [ObservableProperty]
    private int _scanProgressTotal;

    [ObservableProperty]
    private string _scanProgressCategory = string.Empty;

    [ObservableProperty]
    private long _scanProgressBytesScanned;

    [ObservableProperty]
    private int _scanProgressFilesScanned;

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
    private bool _useRecycleBin;

    [ObservableProperty]
    private bool _generateCleanupReport;

    [ObservableProperty]
    private bool _skipLockedItems = true;

    [ObservableProperty]
    private bool _repairPermissionsBeforeDelete;

    [ObservableProperty]
    private bool _includeProtectedSystemLocations;

    [ObservableProperty]
    private bool _isRunConfirmationPopupOpen;

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
    private int _celebrationItemsPendingReboot;

    [ObservableProperty]
    private double _celebrationPendingRebootMegabytes;

    [ObservableProperty]
    private int _celebrationCategoryCount;

    [ObservableProperty]
    private string _celebrationCategoryList = string.Empty;

    [ObservableProperty]
    private string _celebrationTimeSavedDisplay = string.Empty;

    [ObservableProperty]
    private string _celebrationDurationDisplay = string.Empty;

    [ObservableProperty]
    private bool _isAutomationPanelVisible;

    [ObservableProperty]
    private bool _isCleanupAutomationEnabled;

    [ObservableProperty]
    private CleanupAutomationIntervalOption? _selectedAutomationInterval;

    [ObservableProperty]
    private CleanupAutomationDeletionModeOption? _selectedAutomationDeletionMode;

    [ObservableProperty]
    private bool _automationIncludeDownloads = false;

    [ObservableProperty]
    private bool _automationIncludeBrowserHistory = false;

    [ObservableProperty]
    private DateTimeOffset? _automationLastRunUtc;

    [ObservableProperty]
    private int _automationTopItemCount = CleanupAutomationSettings.Default.TopItemCount;

    [ObservableProperty]
    private string _automationStatusMessage = "Automation is disabled.";

    [ObservableProperty]
    private bool _hasAutomationChanges;

    [ObservableProperty]
    private bool _isAutomationBusy;

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

    public int AutomationTopItemMinimum
    {
        get => CleanupAutomationSettings.MinimumTopItemCount;
        set
        {
            // Slider bindings occasionally push values back; keep the floor immutable.
            _ = value;
        }
    }

    public int AutomationTopItemMaximum
    {
        get => CleanupAutomationSettings.MaximumTopItemCount;
        set
        {
            // Slider bindings occasionally push values back; keep the ceiling immutable.
            _ = value;
        }
    }

    public string AutomationTopItemCountDisplay => string.Format(
        CultureInfo.CurrentCulture,
        "{0:N0} items",
        AutomationTopItemCount);

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

    public IReadOnlyList<CleanupExtensionFilterOption> ExtensionFilterOptions => _extensionFilterModel.FilterOptions;

    public IReadOnlyList<CleanupExtensionProfile> ExtensionProfiles => _extensionFilterModel.Profiles;

    public IReadOnlyList<CleanupPreviewSortOption> SortOptions { get; }

    public IReadOnlyList<CleanupAgeFilterOption> AgeFilterOptions { get; }

    public IReadOnlyList<CleanupAutomationIntervalOption> AutomationIntervalOptions { get; }

    public IReadOnlyList<CleanupAutomationDeletionModeOption> AutomationDeletionModeOptions { get; }

    public CleanupExtensionFilterMode SelectedExtensionFilterMode
    {
        get => _extensionFilterModel.Mode;
        set => _extensionFilterModel.Mode = value;
    }

    public CleanupExtensionProfile? SelectedExtensionProfile
    {
        get => _extensionFilterModel.SelectedProfile;
        set => _extensionFilterModel.SelectedProfile = value;
    }

    public string CustomExtensionInput
    {
        get => _extensionFilterModel.CustomInput;
        set => _extensionFilterModel.CustomInput = value ?? string.Empty;
    }

    public event EventHandler? AdministratorRestartRequested;

    public Func<string, bool>? ConfirmElevation { get; set; }

    public bool HasResults => Targets.Count > 0;

    public bool HasFilteredResults => FilteredItems.Count > 0;

    public bool IsSetupPhase => CurrentPhase == CleanupPhase.Setup;

    public bool IsPreviewPhase => CurrentPhase == CleanupPhase.Preview;

    public bool IsCelebrationPhase => CurrentPhase == CleanupPhase.Celebration;

    public bool IsExtensionSelectorEnabled => _extensionFilterModel.IsSelectorEnabled;

    public bool HasCelebrationFailures => CelebrationFailures.Count > 0;

    public bool HasCelebrationCategories => CelebrationCategories.Count > 0;

    public int CelebrationFailureCount => CelebrationFailures.Count;

    public string CelebrationFailureSummary
    {
        get
        {
            var count = CelebrationFailures.Count;
            if (count == 0)
            {
                return string.Empty;
            }

            var first = CelebrationFailures.First();
            if (count == 1)
            {
                return $"{first.DisplayName}: {first.Reason}";
            }

            return $"{first.DisplayName}: {first.Reason} (+{count - 1} more)";
        }
    }

    public IEnumerable<CleanupCelebrationFailureViewModel> CelebrationFailuresPreview =>
        CelebrationFailures.Take(3);

    public bool HasMoreCelebrationFailures => CelebrationFailures.Count > 3;

    public int MoreCelebrationFailuresCount => Math.Max(0, CelebrationFailures.Count - 3);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cleanupAutomationScheduler.SettingsChanged -= OnCleanupAutomationSettingsChanged;
        _relativeTimeTicker.Tick -= OnRelativeTimeTick;
        _previewPagingController.StateChanged -= OnPreviewPagingStateChanged;
        _extensionFilterModel.PropertyChanged -= OnExtensionFilterPropertyChanged;
        _extensionFilterModel.FilterChanged -= OnExtensionFilterChanged;

        CelebrationFailures.CollectionChanged -= OnCelebrationFailuresCollectionChanged;
        _pendingDeletionCategories.CollectionChanged -= OnPendingDeletionCategoriesCollectionChanged;
        _celebrationCategories.CollectionChanged -= OnCelebrationCategoriesCollectionChanged;
        LockingProcesses.CollectionChanged -= OnLockingProcessesCollectionChanged;

        foreach (var process in LockingProcesses)
        {
            process.PropertyChanged -= OnLockingProcessPropertyChanged;
        }

        foreach (var group in Targets)
        {
            group.SelectionChanged -= OnGroupSelectionChanged;
            group.ItemsChanged -= OnGroupItemsChanged;
        }

        _refreshToastCancellation?.Cancel();
        _refreshToastCancellation?.Dispose();
        _refreshToastCancellation = null;

        _phaseTransitionCancellation?.Cancel();
        _phaseTransitionCancellation?.Dispose();
        _phaseTransitionCancellation = null;

        _lockInspectionCancellation?.Cancel();
        _lockInspectionCancellation?.Dispose();
        _lockInspectionCancellation = null;
    }

    public bool CanReviewCelebrationReport => !string.IsNullOrWhiteSpace(CelebrationReportPath) && File.Exists(CelebrationReportPath);

    public string CelebrationReclaimedDisplay => FormatSize(CelebrationReclaimedMegabytes);

    public string CelebrationCategoryListDisplay => string.IsNullOrWhiteSpace(CelebrationCategoryList) ? "—" : CelebrationCategoryList;

    public string ExtensionStatusText => _extensionFilterModel.ExtensionStatusText;

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

            // Show scan progress if available
            if (IsBusy && ScanProgressTotal > 0)
            {
                var bytesDisplay = FormatSizeBytes(ScanProgressBytesScanned);
                return $"{ScanProgressCurrent:N0} of {ScanProgressTotal:N0} locations • {ScanProgressFilesScanned:N0} files ({bytesDisplay})";
            }

            return string.IsNullOrWhiteSpace(BusyStatusDetail)
                ? "Hold tight, this step only takes a moment."
                : BusyStatusDetail;
        }
    }

    public int ActiveOperationProgressValue
    {
        get
        {
            if (IsDeleting)
            {
                return Math.Min(DeletionProgressCurrent, DeletionProgressTotal);
            }

            if (IsBusy && ScanProgressTotal > 0)
            {
                return Math.Min(ScanProgressCurrent, ScanProgressTotal);
            }

            return 0;
        }
    }

    public int ActiveOperationProgressMaximum
    {
        get
        {
            if (IsDeleting && DeletionProgressTotal > 0)
            {
                return DeletionProgressTotal;
            }

            if (IsBusy && ScanProgressTotal > 0)
            {
                return ScanProgressTotal;
            }

            return 100;
        }
    }

    public bool IsActiveOperationIndeterminate
    {
        get
        {
            if (IsDeleting)
            {
                return DeletionProgressTotal <= 0;
            }

            if (IsBusy)
            {
                return ScanProgressTotal <= 0;
            }

            return true;
        }
    }

    public string ActiveOperationPercentDisplay
    {
        get
        {
            if (IsDeleting && DeletionProgressTotal > 0)
            {
                var percent = (double)Math.Min(DeletionProgressCurrent, DeletionProgressTotal) / DeletionProgressTotal;
                return percent >= 1d ? "100%" : percent.ToString("P0");
            }

            if (IsBusy && ScanProgressTotal > 0)
            {
                var percent = (double)Math.Min(ScanProgressCurrent, ScanProgressTotal) / ScanProgressTotal;
                return percent >= 1d ? "100%" : percent.ToString("P0");
            }

            return string.Empty;
        }
    }

    public bool HasActiveOperationPercent => (IsDeleting && DeletionProgressTotal > 0) || (IsBusy && ScanProgressTotal > 0);

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
            IsPreviewScanInProgress = true;
            BusyStatusMessage = "Analyzing cleanup targets…";
            BusyStatusDetail = $"Reviewing up to {PreviewCount:N0} top items";

            // Reset scan progress
            ScanProgressCurrent = 0;
            ScanProgressTotal = 0;
            ScanProgressCategory = string.Empty;
            ScanProgressBytesScanned = 0;
            ScanProgressFilesScanned = 0;

            ClearTargets();
            CurrentPage = 1;

            // Create progress reporter for scan operation
            var scanProgress = new Progress<TidyWindow.Core.Cleanup.CleanupScanProgress>(progress =>
            {
                ScanProgressCurrent = progress.CompletedTargets;
                ScanProgressTotal = progress.TotalTargets;
                ScanProgressCategory = progress.CurrentCategory;
                ScanProgressBytesScanned = progress.TotalBytesScanned;
                ScanProgressFilesScanned = progress.TotalFilesScanned;

                // Update computed properties
                OnPropertyChanged(nameof(ActiveOperationDetail));
                OnPropertyChanged(nameof(ActiveOperationProgressValue));
                OnPropertyChanged(nameof(ActiveOperationProgressMaximum));
                OnPropertyChanged(nameof(IsActiveOperationIndeterminate));
                OnPropertyChanged(nameof(ActiveOperationPercentDisplay));
                OnPropertyChanged(nameof(HasActiveOperationPercent));

                if (!string.IsNullOrWhiteSpace(progress.CurrentCategory))
                {
                    BusyStatusMessage = $"Scanning {progress.CurrentCategory}…";
                }
            });

            var report = await Task.Run(
                () => _cleanupService.PreviewAsync(IncludeDownloads, IncludeBrowserHistory, PreviewCount, SelectedItemKind, scanProgress, CancellationToken.None),
                CancellationToken.None).ConfigureAwait(true);

            var previewPrep = await Task.Run(() =>
            {
                var filtered = FilterPreviewTargets(report.Targets)
                    .OrderByDescending(static t => t.TotalSizeBytes)
                    .ToList();

                var total = filtered.Sum(static target => target.ItemCount);
                var delta = _hasCompletedPreview ? Math.Max(0, total - _lastPreviewTotalItemCount) : 0;

                return new PreviewMaterialization(filtered, total, delta);
            }).ConfigureAwait(true);

            _lastPreviewTotalItemCount = previewPrep.TotalItems;

            if (Targets.Count > 0)
            {
                ClearTargets();
            }

            await AddTargetGroupsAsync(previewPrep.Targets);

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
            HandleRefreshToast(previewPrep.NewItems, previewPrep.TotalItems);

            var status = Targets.Count == 0
                ? "No cleanup targets detected."
                : $"Preview ready: {SummaryText}";

            var warningCount = previewPrep.Targets.Sum(static target => target.Warnings.Count);
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

            // Reset scan progress
            ScanProgressCurrent = 0;
            ScanProgressTotal = 0;
            ScanProgressCategory = string.Empty;
            ScanProgressBytesScanned = 0;
            ScanProgressFilesScanned = 0;

            IsPreviewScanInProgress = false;
            IsBusy = false;
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectionSummaryText));
            OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        if (IsBusy || IsDeletionPreparationInProgress)
        {
            return;
        }

        var itemsToDelete = Targets
            .SelectMany(static group => group.SelectedItems.Select(item => (group, item)))
            .ToList();

        if (itemsToDelete.Count == 0)
        {
            return;
        }

        var previousBusyMessage = BusyStatusMessage;
        var previousBusyDetail = BusyStatusDetail;

        IsDeletionPreparationInProgress = true;
        BusyStatusMessage = "Preparing confirmation…";
        BusyStatusDetail = "Summarizing your selection before showing the sheet.";

        try
        {
            // Let the dispatcher paint the overlay before any heavier UI-thread work runs.
            await Dispatcher.Yield(DispatcherPriority.Render);

            // Force a UI pass so the overlay renders before prep.
            var dispatcher = Dispatcher.FromThread(Thread.CurrentThread) ?? Dispatcher.CurrentDispatcher;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            PrepareDeletionConfirmation(itemsToDelete);
        }
        finally
        {
            BusyStatusMessage = previousBusyMessage;
            BusyStatusDetail = previousBusyDetail;
            IsDeletionPreparationInProgress = false;
        }
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

    private bool CanConfirmCleanup() => !IsBusy && !IsCleanupExecutionInProgress && _pendingDeletionItems is { Count: > 0 } && IsConfirmationSheetVisible;

    [RelayCommand(CanExecute = nameof(CanConfirmCleanup))]
    private async Task ConfirmCleanupAsync()
    {
        if (_pendingDeletionItems is null || _pendingDeletionItems.Count == 0)
        {
            ClearPendingDeletionState();
            return;
        }

        IsCleanupExecutionInProgress = true;
        IsRunConfirmationPopupOpen = false;

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
            // When recycle is requested, avoid silently falling back to permanent delete.
            AllowPermanentDeleteFallback = !useRecycleBin,
            SkipLockedItems = SkipLockedItems,
            TakeOwnershipOnAccessDenied = RepairPermissionsBeforeDelete,
            AllowProtectedSystemPaths = IncludeProtectedSystemLocations
        };

        await ExecuteDeletionAsync(snapshot, deletionOptions, generateReport);
    }

    private bool CanShowRunConfirmationPopup() => CanConfirmCleanup();

    [RelayCommand(CanExecute = nameof(CanShowRunConfirmationPopup))]
    private void ShowRunConfirmationPopup()
    {
        if (!IsRunConfirmationPopupOpen)
        {
            IsRunConfirmationPopupOpen = true;
        }
    }

    [RelayCommand]
    private void HideRunConfirmationPopup()
    {
        if (IsRunConfirmationPopupOpen)
        {
            IsRunConfirmationPopupOpen = false;
        }
    }

    private bool CanShowPreviewConfirmationPopup() => !IsBusy && !IsPreviewConfirmationPopupOpen;

    [RelayCommand(CanExecute = nameof(CanShowPreviewConfirmationPopup))]
    private void ShowPreviewConfirmationPopup()
    {
        if (!IsPreviewConfirmationPopupOpen)
        {
            IsPreviewConfirmationPopupOpen = true;
        }
    }

    [RelayCommand]
    private void HidePreviewConfirmationPopup()
    {
        if (IsPreviewConfirmationPopupOpen)
        {
            IsPreviewConfirmationPopupOpen = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmAndRunPreviewAsync()
    {
        IsPreviewConfirmationPopupOpen = false;
        await RunPreviewAsync();
    }

    private bool CanDeleteSelected() => !IsBusy && !IsDeletionPreparationInProgress && HasSelection && !IsConfirmationSheetVisible;

    private void PrepareDeletionConfirmation(List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> itemsToDelete)
    {
        _pendingDeletionItems = itemsToDelete;
        PendingDeletionItemCount = itemsToDelete.Count;
        PendingDeletionTotalSizeMegabytes = itemsToDelete.Sum(static tuple => tuple.item.SizeMegabytes);

        var categoryNames = NormalizeDistinctCategories(itemsToDelete.Select(static tuple => tuple.group.Category));

        PendingDeletionCategoryCount = categoryNames.Count;
        UpdateCategoryCollection(_pendingDeletionCategories, categoryNames);

        PendingDeletionCategoryList = BuildCategoryListText(categoryNames);

        UseRecycleBin = false;
        GenerateCleanupReport = false;
        SkipLockedItems = true;
        RepairPermissionsBeforeDelete = false;
        IncludeProtectedSystemLocations = false;
        IsRunConfirmationPopupOpen = false;

        BuildPendingDeletionRisks(itemsToDelete);

        IsConfirmationSheetVisible = true;
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
        ShowRunConfirmationPopupCommand.NotifyCanExecuteChanged();

        BeginLockInspection(itemsToDelete);
    }

    private void ClearPendingDeletionState()
    {
        CancelLockInspection();
        LockingProcesses.Clear();
        LockInspectionStatusMessage = string.Empty;
        IsLockingProcessPopupOpen = false;
        IsRunConfirmationPopupOpen = false;
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
        ShowRunConfirmationPopupCommand.NotifyCanExecuteChanged();
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
            return CleanupSystemPathSafety.IsSystemCriticalPath(path);
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

        var requiresElevation = itemsToDelete.Any(static tuple => CleanupSystemPathSafety.IsSystemCriticalPath(tuple.item.Model.FullName));
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

        var workDescription = BuildCleanupWorkDescription(itemsToDelete.Count, totalSizeMb);
        Guid workToken = Guid.Empty;

        var manualBrowserEntries = new List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item, CleanupDeletionEntry entry)>();
        BrowserHistoryHandleResult browserHistoryResult = BrowserHistoryHandleResult.Empty;

        try
        {
            workToken = _workTracker.BeginWork(AutomationWorkType.Cleanup, workDescription);
            IsDeleting = true;
            IsBusy = true;

            if (_browserCleanupService is not null)
            {
                browserHistoryResult = await HandleBrowserHistoryAsync(itemsToDelete, CancellationToken.None);
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

                    case CleanupDeletionDisposition.PendingReboot:
                        // Items scheduled for reboot deletion should be removed from UI
                        // but users need to restart for space to be actually freed
                        removalCandidates.Add(tuple);
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

            LogCleanupActivity(
                combinedResult,
                deletionOptions,
                stopwatch.Elapsed,
                itemsToDelete.Count,
                totalSizeMb,
                reportPath,
                reportError);

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
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Cleanup", "Delete failed", new[] { ex.ToString() });
            DeletionStatusMessage = ex.Message;
        }
        finally
        {
            BusyStatusMessage = "Working…";
            BusyStatusDetail = string.Empty;
            IsDeleting = false;
            IsCleanupExecutionInProgress = false;
            IsBusy = false;
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            ConfirmCleanupCommand.NotifyCanExecuteChanged();
            ShowRunConfirmationPopupCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectionSummaryText));
            OnPropertyChanged(nameof(IsCurrentCategoryFullySelected));

            if (workToken != Guid.Empty)
            {
                _workTracker.CompleteWork(workToken);
            }
        }
    }

    private static string BuildCleanupWorkDescription(int itemCount, double totalSizeMb)
    {
        if (itemCount <= 0)
        {
            return "Cleanup run";
        }

        if (totalSizeMb > 0.05)
        {
            return $"Cleanup run ({itemCount:N0} items, {totalSizeMb:F1} MB)";
        }

        return $"Cleanup run ({itemCount:N0} items)";
    }

    private async Task<BrowserHistoryHandleResult> HandleBrowserHistoryAsync(
        IReadOnlyList<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> items,
        CancellationToken cancellationToken)
    {
        if (items is null || items.Count == 0)
        {
            return BrowserHistoryHandleResult.Empty;
        }

        var grouped = new Dictionary<string, (BrowserProfile Profile, List<(CleanupTargetGroupViewModel group, CleanupPreviewItemViewModel item)> Items)>(StringComparer.OrdinalIgnoreCase);

        foreach (var tuple in items)
        {
            if (tuple.item is null || !string.Equals(tuple.item.Classification, "History", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidatePath = tuple.item.Model.FullName;
            if (!BrowserHistoryHelper.TryGetBrowserProfile(candidatePath, out var profile))
            {
                continue;
            }

            if (!grouped.TryGetValue(profile.ProfileDirectory, out var bucket))
            {
                bucket = (profile, new List<(CleanupTargetGroupViewModel, CleanupPreviewItemViewModel)>());
                grouped[profile.ProfileDirectory] = bucket;
            }

            bucket.Items.Add(tuple);
            grouped[profile.ProfileDirectory] = bucket;
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

            var profile = kvp.Value.Profile;
            var targets = kvp.Value.Items.Select(static tuple => tuple.item.Model.FullName).ToList();
            var result = await _browserCleanupService.ClearHistoryAsync(profile, targets, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                var dispositionMessage = profile.Kind switch
                {
                    BrowserKind.Edge => "Cleared safely via Microsoft Edge API.",
                    BrowserKind.Chrome => "Cleared via Chrome history purge.",
                    _ => "Cleared browser history."
                };

                foreach (var tuple in kvp.Value.Items)
                {
                    var entry = new CleanupDeletionEntry(
                        tuple.item.Model.FullName,
                        tuple.item.Model.SizeBytes,
                        tuple.item.IsDirectory,
                        CleanupDeletionDisposition.Deleted,
                        dispositionMessage);
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

    private void OnCleanupAutomationSettingsChanged(object? sender, CleanupAutomationSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyAutomationSettingsSnapshot(settings);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => ApplyAutomationSettingsSnapshot(settings)));
        }
    }

    private void ApplyAutomationSettingsSnapshot(CleanupAutomationSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        _suspendAutomationStateUpdates = true;

        IsCleanupAutomationEnabled = settings.AutomationEnabled;

        SelectedAutomationInterval = AutomationIntervalOptions.FirstOrDefault(option => option.Minutes == settings.IntervalMinutes)
            ?? AutomationIntervalOptions.FirstOrDefault();

        SelectedAutomationDeletionMode = AutomationDeletionModeOptions.FirstOrDefault(option => option.Mode == settings.DeletionMode)
            ?? AutomationDeletionModeOptions.FirstOrDefault();

        AutomationIncludeDownloads = settings.IncludeDownloads;
        AutomationIncludeBrowserHistory = settings.IncludeBrowserHistory;
        AutomationTopItemCount = settings.TopItemCount;
        AutomationLastRunUtc = settings.LastRunUtc;
        HasAutomationChanges = false;

        _suspendAutomationStateUpdates = false;
        UpdateAutomationStatus();
    }

    private CleanupAutomationSettings BuildAutomationSettingsSnapshot()
    {
        var interval = SelectedAutomationInterval?.Minutes ?? _cleanupAutomationScheduler.CurrentSettings.IntervalMinutes;
        var mode = SelectedAutomationDeletionMode?.Mode ?? CleanupAutomationDeletionMode.SkipLocked;
        var lastRun = _cleanupAutomationScheduler.CurrentSettings.LastRunUtc;
        var topItems = AutomationTopItemCount > 0
            ? AutomationTopItemCount
            : _cleanupAutomationScheduler.CurrentSettings.TopItemCount;

        return new CleanupAutomationSettings(
            IsCleanupAutomationEnabled,
            interval,
            mode,
            AutomationIncludeDownloads,
            AutomationIncludeBrowserHistory,
            topItems,
            lastRun);
    }

    private void UpdateAutomationStatus()
    {
        if (!IsCleanupAutomationEnabled)
        {
            AutomationStatusMessage = "Automation is disabled.";
            return;
        }

        var intervalLabel = SelectedAutomationInterval?.Label
                            ?? FormatInterval(SelectedAutomationInterval?.Minutes ?? _cleanupAutomationScheduler.CurrentSettings.IntervalMinutes);

        var topItems = AutomationTopItemCount > 0
            ? AutomationTopItemCount
            : _cleanupAutomationScheduler.CurrentSettings.TopItemCount;

        var includeParts = new List<string>();
        if (AutomationIncludeDownloads)
        {
            includeParts.Add("downloads");
        }

        if (AutomationIncludeBrowserHistory)
        {
            includeParts.Add("history");
        }

        if (includeParts.Count == 0)
        {
            includeParts.Add("system caches");
        }

        var lastRunText = AutomationLastRunUtc is null
            ? "Hasn't run yet."
            : $"Last run {FormatRelativeTime(AutomationLastRunUtc.Value)}.";

        var sweepText = $"Sweeps top {topItems} items";

        AutomationStatusMessage = $"{intervalLabel} • {sweepText} • Targets {string.Join(" + ", includeParts)} • {lastRunText}";
    }

    private void OnRelativeTimeTick(object? sender, EventArgs e)
    {
        UpdateAutomationStatus();
    }

    private void MarkAutomationStateDirty()
    {
        if (_suspendAutomationStateUpdates)
        {
            return;
        }

        HasAutomationChanges = true;
        UpdateAutomationStatus();
    }

    private static string FormatInterval(int intervalMinutes)
    {
        if (intervalMinutes <= 0)
        {
            return "Custom interval";
        }

        var span = TimeSpan.FromMinutes(intervalMinutes);
        if (span.TotalDays >= 30)
        {
            return "Every month";
        }

        if (span.TotalDays >= 7)
        {
            return "Every week";
        }

        if (span.TotalDays >= 1)
        {
            return span.TotalDays == 1 ? "Every day" : $"Every {span.TotalDays:F0} days";
        }

        if (span.TotalHours >= 1)
        {
            return span.TotalHours == 1 ? "Every hour" : $"Every {span.TotalHours:F0} hours";
        }

        return span.TotalMinutes == 1 ? "Every minute" : $"Every {span.TotalMinutes:F0} minutes";
    }

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta < TimeSpan.FromMinutes(1))
        {
            return "moments ago";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)Math.Round(delta.TotalMinutes))} min ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)Math.Round(delta.TotalHours))} hr ago";
        }

        if (delta < TimeSpan.FromDays(30))
        {
            return $"{Math.Max(1, (int)Math.Round(delta.TotalDays))} day(s) ago";
        }

        return timestamp.ToLocalTime().ToString("g");
    }

    private IReadOnlyList<string> BuildAutomationSettingsDetails(CleanupAutomationSettings settings)
    {
        var details = new List<string>
        {
            $"Cadence: {ResolveAutomationIntervalLabel(settings.IntervalMinutes)}",
            $"Deletion mode: {ResolveAutomationDeletionModeLabel(settings.DeletionMode)}",
            $"Scope: {BuildScopeSummary(settings.IncludeDownloads, settings.IncludeBrowserHistory)}"
        };

        if (settings.LastRunUtc is DateTimeOffset lastRun)
        {
            details.Add($"Last run: {FormatRelativeTime(lastRun)}");
        }
        else
        {
            details.Add("Last run: not yet");
        }

        return details;
    }

    private IReadOnlyList<string> BuildAutomationRunDetails(CleanupAutomationRunResult result)
    {
        var details = new List<string>
        {
            $"Requested items: {result.RequestedItemCount:N0}",
            $"Requested size: {FormatSize(result.RequestedBytes / 1_048_576d)}",
            $"Deleted: {result.DeletionResult.DeletedCount:N0} ({FormatSize(result.DeletionResult.TotalBytesDeleted / 1_048_576d)})",
            $"Skipped: {result.DeletionResult.SkippedCount:N0} ({FormatSize(result.DeletionResult.TotalBytesSkipped / 1_048_576d)})",
            $"Failed: {result.DeletionResult.FailedCount:N0} ({FormatSize(result.DeletionResult.TotalBytesFailed / 1_048_576d)})"
        };

        if (result.Warnings.Count > 0)
        {
            details.Add("Warnings:");
            foreach (var warning in result.Warnings.Take(5))
            {
                details.Add(" • " + warning);
            }
        }

        if (result.DeletionResult.HasErrors)
        {
            details.Add("Errors:");
            foreach (var error in result.DeletionResult.Errors.Take(5))
            {
                details.Add(" • " + error);
            }
        }

        return details;
    }

    private string ResolveAutomationIntervalLabel(int minutes)
    {
        var label = AutomationIntervalOptions.FirstOrDefault(option => option.Minutes == minutes)?.Label;
        return string.IsNullOrWhiteSpace(label) ? FormatInterval(minutes) : label!;
    }

    private string ResolveAutomationDeletionModeLabel(CleanupAutomationDeletionMode mode)
    {
        return AutomationDeletionModeOptions.FirstOrDefault(option => option.Mode == mode)?.Label
            ?? mode.ToString();
    }

    private static string BuildScopeSummary(bool includeDownloads, bool includeBrowserHistory)
    {
        if (includeDownloads && includeBrowserHistory)
        {
            return "Downloads + history";
        }

        if (includeDownloads)
        {
            return "Downloads only";
        }

        if (includeBrowserHistory)
        {
            return "History only";
        }

        return "System caches only";
    }

    private sealed record SkipReasonStat(string Reason, int Count, long TotalBytes);
    private const string UnspecifiedSkipReasonLabel = "Unspecified skip reason";

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

    private void LogCleanupActivity(
        CleanupDeletionResult result,
        CleanupDeletionOptions options,
        TimeSpan duration,
        int requestedItems,
        double requestedMegabytes,
        string? reportPath,
        string? reportError)
    {
        if (result is null)
        {
            return;
        }

        var reclaimedMegabytes = Math.Max(result.TotalBytesDeleted / 1_048_576d, 0d);
        var skippedMegabytes = Math.Max(result.TotalBytesSkipped / 1_048_576d, 0d);
        var failedMegabytes = Math.Max(result.TotalBytesFailed / 1_048_576d, 0d);
        var deleteOnRebootAllowed = options.AllowDeleteOnReboot || options.TakeOwnershipOnAccessDenied;
        var rebootEntries = result.Entries.Where(static entry => IsDeleteOnRebootEntry(entry)).ToList();
        var skipReasonStats = BuildSkipReasonStats(result);

        var details = new List<string>
        {
            $"Elapsed: {duration.TotalSeconds:F2}s",
            $"Requested items: {requestedItems:N0}",
            $"Requested size: {FormatSize(Math.Max(requestedMegabytes, 0d))}",
            $"Deleted: {result.DeletedCount:N0} ({FormatSize(reclaimedMegabytes)})",
            $"Skipped: {result.SkippedCount:N0} ({FormatSize(skippedMegabytes)})",
            $"Failed: {result.FailedCount:N0} ({FormatSize(failedMegabytes)})",
            $"Force delete enabled: {options.TakeOwnershipOnAccessDenied}",
            $"Include protected system locations: {options.AllowProtectedSystemPaths}",
            $"Skip locked items: {options.SkipLockedItems}",
            $"Delete-on-reboot allowed: {deleteOnRebootAllowed}"
        };

        if (skipReasonStats.Count > 0)
        {
            details.Add("Skip reasons:");
            foreach (var stat in skipReasonStats.Take(5))
            {
                var sizeText = stat.TotalBytes > 0
                    ? $" ({FormatSize(stat.TotalBytes / 1_048_576d)})"
                    : string.Empty;
                details.Add($"  ↳ {stat.Reason}: {stat.Count:N0} item(s){sizeText}");
            }

            if (skipReasonStats.Count > 5)
            {
                details.Add($"  (+{skipReasonStats.Count - 5:N0} additional reason(s))");
            }
        }
        else if (result.SkippedCount > 0)
        {
            details.Add("Skip reasons: Not reported by deletion engine.");
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            details.Add($"Report path: {reportPath}");
        }
        else if (!string.IsNullOrWhiteSpace(reportError))
        {
            details.Add($"Report failed: {reportError}");
        }

        if (deleteOnRebootAllowed)
        {
            if (rebootEntries.Count > 0)
            {
                details.Add($"Delete-on-reboot scheduled for {rebootEntries.Count:N0} item(s).");
                foreach (var entry in rebootEntries.Take(5))
                {
                    details.Add($"  ↳ {entry.Path}");
                }

                if (rebootEntries.Count > 5)
                {
                    details.Add($"  (+{rebootEntries.Count - 5:N0} more)");
                }
            }
            else
            {
                details.Add("Delete-on-reboot allowed but not needed.");
            }
        }
        else
        {
            details.Add("Delete-on-reboot disabled for this cleanup run.");
        }

        var level = result.FailedCount > 0 ? ActivityLogLevel.Warning : ActivityLogLevel.Success;
        var summary = result.FailedCount > 0
            ? $"Cleanup completed with {result.FailedCount:N0} failure(s)."
            : $"Cleanup completed — {FormatSize(reclaimedMegabytes)} reclaimed.";

        if (result.SkippedCount > 0)
        {
            summary += $" Skipped {result.SkippedCount:N0}.";
        }

        _mainViewModel.LogActivity(level, "Cleanup", summary, details);
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
        var pendingRebootMegabytes = deletionResult.TotalBytesPendingReboot / 1_048_576d;
        CelebrationItemsDeleted = deletionResult.DeletedCount;
        CelebrationItemsSkipped = deletionResult.SkippedCount;
        CelebrationItemsFailed = deletionResult.FailedCount;
        CelebrationItemsPendingReboot = deletionResult.PendingRebootCount;
        CelebrationPendingRebootMegabytes = pendingRebootMegabytes;
        CelebrationReclaimedMegabytes = reclaimedMegabytes;
        var normalizedCategories = NormalizeDistinctCategories(categoriesTouched);
        CelebrationCategoryCount = normalizedCategories.Count;
        UpdateCategoryCollection(_celebrationCategories, normalizedCategories);
        CelebrationCategoryList = BuildCategoryListText(normalizedCategories);

        // Build headline that accurately reflects what was actually freed
        CelebrationHeadline = BuildCelebrationHeadline(reclaimedMegabytes, pendingRebootMegabytes, deletionResult.DeletedCount, deletionResult.PendingRebootCount);

        CelebrationDetails = BuildCelebrationDetails(deletionResult, CelebrationCategoryCount, reclaimedMegabytes);
        CelebrationDurationDisplay = FormatDuration(executionDuration);
        var estimatedTimeSaved = EstimateTimeSaved(deletionResult.DeletedCount, deletionResult.TotalBytesDeleted, executionDuration);
        CelebrationTimeSavedDisplay = FormatDuration(estimatedTimeSaved);

        CelebrationFailures.Clear();
        IsCelebrationErrorsPopupOpen = false;
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

    private static IReadOnlyList<SkipReasonStat> BuildSkipReasonStats(CleanupDeletionResult result)
    {
        if (result?.Entries is null || result.Entries.Count == 0)
        {
            return Array.Empty<SkipReasonStat>();
        }

        var aggregate = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in result.Entries)
        {
            if (entry is null || entry.Disposition != CleanupDeletionDisposition.Skipped)
            {
                continue;
            }

            var reason = string.IsNullOrWhiteSpace(entry.Reason)
                ? UnspecifiedSkipReasonLabel
                : entry.Reason.Trim();

            var size = Math.Max(entry.SizeBytes, 0);

            if (aggregate.TryGetValue(reason, out var current))
            {
                aggregate[reason] = (current.Count + 1, current.Bytes + size);
            }
            else
            {
                aggregate[reason] = (1, size);
            }
        }

        if (aggregate.Count == 0)
        {
            return Array.Empty<SkipReasonStat>();
        }

        return aggregate
            .Select(static pair => new SkipReasonStat(pair.Key, pair.Value.Count, pair.Value.Bytes))
            .OrderByDescending(static stat => stat.Count)
            .ThenByDescending(static stat => stat.TotalBytes)
            .ThenBy(static stat => stat.Reason, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    /// <summary>
    /// Builds an accurate headline that distinguishes between immediately freed space and pending reboot space.
    /// </summary>
    private static string BuildCelebrationHeadline(double reclaimedMegabytes, double pendingRebootMegabytes, int deletedCount, int pendingRebootCount)
    {
        var hasImmediateReclaim = reclaimedMegabytes > 0.01;
        var hasPendingReclaim = pendingRebootMegabytes > 0.01 && pendingRebootCount > 0;

        if (hasImmediateReclaim && hasPendingReclaim)
        {
            // Both immediate and pending reclaim
            return $"Cleanup complete — {FormatSize(reclaimedMegabytes)} reclaimed ({FormatSize(pendingRebootMegabytes)} after restart)";
        }

        if (hasImmediateReclaim)
        {
            // Only immediate reclaim
            return $"Cleanup complete — {FormatSize(reclaimedMegabytes)} reclaimed";
        }

        if (hasPendingReclaim)
        {
            // Only pending reclaim - space will be freed after restart
            return $"Cleanup complete — {FormatSize(pendingRebootMegabytes)} will free after restart";
        }

        if (deletedCount > 0 || pendingRebootCount > 0)
        {
            // Items processed but minimal size
            return "Cleanup complete";
        }

        return "No items removed";
    }

    private static string BuildCelebrationDetails(CleanupDeletionResult result, int categoryCount, double reclaimedMegabytes)
    {
        var parts = new List<string>();

        if (result.DeletedCount > 0)
        {
            parts.Add($"{result.DeletedCount:N0} item(s) removed");
        }

        // Show pending reboot items separately
        if (result.PendingRebootCount > 0)
        {
            var pendingMb = result.TotalBytesPendingReboot / 1_048_576d;
            if (pendingMb > 0.01)
            {
                parts.Add($"{result.PendingRebootCount:N0} item(s) pending restart ({FormatSize(pendingMb)})");
            }
            else
            {
                parts.Add($"{result.PendingRebootCount:N0} item(s) pending restart");
            }
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

        // Include pending reboot info if applicable
        if (result.PendingRebootCount > 0)
        {
            var pendingMb = result.TotalBytesPendingReboot / 1_048_576d;
            builder.AppendLine($"Pending restart: {result.PendingRebootCount:N0} item(s) ({FormatSize(pendingMb)})");
        }

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
        IncludeDownloads = false;
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
    private void ToggleAutomationPanel()
    {
        IsAutomationPanelVisible = !IsAutomationPanelVisible;
    }

    [RelayCommand]
    private void CloseAutomationPanel()
    {
        if (IsAutomationPanelVisible)
        {
            IsAutomationPanelVisible = false;
        }
    }

    [RelayCommand]
    private async Task ApplyCleanupAutomationAsync()
    {
        if (IsAutomationBusy)
        {
            return;
        }

        try
        {
            IsAutomationBusy = true;
            _mainViewModel.SetStatusMessage("Saving cleanup automation...");
            var snapshot = BuildAutomationSettingsSnapshot();
            await _cleanupAutomationScheduler.ApplySettingsAsync(snapshot, runImmediately: false);
            HasAutomationChanges = false;

            var status = snapshot.AutomationEnabled
                ? $"Cleanup automation enabled ({FormatInterval(snapshot.IntervalMinutes)})."
                : "Cleanup automation disabled.";
            var level = snapshot.AutomationEnabled ? ActivityLogLevel.Success : ActivityLogLevel.Information;
            _mainViewModel.LogActivity(level, "Cleanup automation", status, BuildAutomationSettingsDetails(snapshot));
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Cleanup automation", "Failed to save automation settings.", new[] { ex.Message });
        }
        finally
        {
            IsAutomationBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private async Task RunCleanupAutomationNowAsync()
    {
        if (IsAutomationBusy || !IsCleanupAutomationEnabled)
        {
            return;
        }

        try
        {
            IsAutomationBusy = true;
            _mainViewModel.SetStatusMessage("Running cleanup automation...");
            var result = await _cleanupAutomationScheduler.RunOnceAsync();
            var logLevel = result.WasSkipped ? ActivityLogLevel.Warning : ActivityLogLevel.Success;
            var message = result.WasSkipped
                ? $"Automation run skipped — {result.Message}"
                : $"Automation run complete — {result.Message}";
            _mainViewModel.LogActivity(logLevel, "Cleanup automation", message, BuildAutomationRunDetails(result));
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Cleanup automation", "Failed to run automation.", new[] { ex.Message });
        }
        finally
        {
            IsAutomationBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    [RelayCommand]
    private void DismissRefreshToast()
    {
        HideRefreshToast();
    }



    partial void OnIsBusyChanged(bool value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ConfirmCleanupCommand.NotifyCanExecuteChanged();
        ShowRunConfirmationPopupCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDeletionPreparationInProgressChanged(bool value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
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

    partial void OnIsCleanupAutomationEnabledChanged(bool value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnSelectedAutomationIntervalChanged(CleanupAutomationIntervalOption? value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnSelectedAutomationDeletionModeChanged(CleanupAutomationDeletionModeOption? value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnAutomationIncludeDownloadsChanged(bool value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnAutomationIncludeBrowserHistoryChanged(bool value)
    {
        MarkAutomationStateDirty();
    }

    partial void OnAutomationTopItemCountChanged(int value)
    {
        OnPropertyChanged(nameof(AutomationTopItemCountDisplay));
        MarkAutomationStateDirty();
    }

    partial void OnAutomationLastRunUtcChanged(DateTimeOffset? value)
    {
        UpdateAutomationStatus();
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
        ShowRunConfirmationPopupCommand.NotifyCanExecuteChanged();
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

    partial void OnRepairPermissionsBeforeDeleteChanged(bool value)
    {
        if (value && SkipLockedItems)
        {
            SkipLockedItems = false;
        }

        if (!value)
        {
            // Never allow the protected system toggle to stay on when force delete is disabled.
            if (IncludeProtectedSystemLocations)
            {
                IncludeProtectedSystemLocations = false;
            }
        }
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
        ShowRunConfirmationPopupCommand.NotifyCanExecuteChanged();
        if (!value)
        {
            IsRunConfirmationPopupOpen = false;
        }
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

    private void OnExtensionFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupExtensionFilterModel.Mode))
        {
            _previewFilter.ExtensionFilterMode = _extensionFilterModel.Mode;
            OnPropertyChanged(nameof(SelectedExtensionFilterMode));
            OnPropertyChanged(nameof(IsExtensionSelectorEnabled));
        }
        else if (e.PropertyName == nameof(CleanupExtensionFilterModel.SelectedProfile))
        {
            OnPropertyChanged(nameof(SelectedExtensionProfile));
        }
        else if (e.PropertyName == nameof(CleanupExtensionFilterModel.CustomInput))
        {
            OnPropertyChanged(nameof(CustomExtensionInput));
        }

        if (e.PropertyName == nameof(CleanupExtensionFilterModel.ExtensionStatusText))
        {
            OnPropertyChanged(nameof(ExtensionStatusText));
        }

        if (e.PropertyName == nameof(CleanupExtensionFilterModel.IsSelectorEnabled))
        {
            OnPropertyChanged(nameof(IsExtensionSelectorEnabled));
        }
    }

    private void OnExtensionFilterChanged(object? sender, EventArgs e)
    {
        _previewFilter.SetActiveExtensions(_extensionFilterModel.ActiveExtensions);
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
        OnPropertyChanged(nameof(CelebrationFailureCount));
        OnPropertyChanged(nameof(CelebrationFailureSummary));
        OnPropertyChanged(nameof(CelebrationFailuresPreview));
        OnPropertyChanged(nameof(HasMoreCelebrationFailures));
        OnPropertyChanged(nameof(MoreCelebrationFailuresCount));
    }

    [RelayCommand]
    private void ShowCelebrationErrorsPopup()
    {
        IsCelebrationErrorsPopupOpen = true;
    }

    [RelayCommand]
    private void HideCelebrationErrorsPopup()
    {
        IsCelebrationErrorsPopupOpen = false;
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

    private async Task AddTargetGroupsAsync(IReadOnlyList<CleanupTargetReport> targets)
    {
        if (targets is null || targets.Count == 0)
        {
            return;
        }

        var materialized = await Task.Run(() =>
            targets
                .Where(static target => target is not null)
                .Select(static target => new CleanupTargetGroupViewModel(target))
                .ToList(),
            CancellationToken.None).ConfigureAwait(true);

        var added = 0;
        foreach (var group in materialized)
        {
            AddTargetGroup(group);
            added++;

            if (added % PreviewUiYieldInterval == 0)
            {
                await Task.Yield();
            }
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

    private void ResetCurrentPage()
    {
        _previewPagingController.ResetCurrentPage();
        OnPropertyChanged(nameof(CurrentPage));
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

    private static bool IsDeleteOnRebootEntry(CleanupDeletionEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Reason))
        {
            return false;
        }

        return entry.Reason.IndexOf("Scheduled for removal", StringComparison.OrdinalIgnoreCase) >= 0;
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

    private static string FormatSizeBytes(long bytes)
    {
        const double KB = 1024d;
        const double MB = KB * 1024d;
        const double GB = MB * 1024d;

        if (bytes >= GB)
        {
            return $"{bytes / GB:F2} GB";
        }

        if (bytes >= MB)
        {
            return $"{bytes / MB:F2} MB";
        }

        if (bytes >= KB)
        {
            return $"{bytes / KB:F0} KB";
        }

        return $"{bytes} bytes";
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

    private static string NormalizeExtension(string? extension)
    {
        return CleanupPreviewFilter.NormalizeExtension(extension);
    }
}
