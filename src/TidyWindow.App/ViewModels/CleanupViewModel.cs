using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
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

public readonly record struct CleanupDeletionConfirmation(int ItemCount, double TotalSizeMegabytes);

public sealed partial class CleanupViewModel : ViewModelBase
{
    private readonly CleanupService _cleanupService;
    private readonly MainViewModel _mainViewModel;
    private readonly IPrivilegeService _privilegeService;

    private readonly HashSet<string> _activeExtensions = new(StringComparer.OrdinalIgnoreCase);
    private int _previewCount = 10;
    private static readonly string[] _sensitiveRoots = BuildSensitiveRoots();

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

    public ObservableCollection<CleanupTargetGroupViewModel> Targets { get; } = new();

    public ObservableCollection<CleanupPreviewItemViewModel> FilteredItems { get; } = new();

    public IReadOnlyList<CleanupItemKindOption> ItemKindOptions { get; }

    public IReadOnlyList<CleanupExtensionFilterOption> ExtensionFilterOptions { get; }

    public IReadOnlyList<CleanupExtensionProfile> ExtensionProfiles { get; }

    public event EventHandler? AdministratorRestartRequested;

    public Func<CleanupDeletionConfirmation, bool>? ConfirmDeletion { get; set; }

    public Func<string, bool>? ConfirmElevation { get; set; }

    public bool HasResults => Targets.Count > 0;

    public bool HasFilteredResults => FilteredItems.Count > 0;

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
            var sanitized = value < 0 ? 0 : value;
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

            var report = await _cleanupService.PreviewAsync(IncludeDownloads, PreviewCount, SelectedItemKind);
            foreach (var target in report.Targets.OrderByDescending(t => t.TotalSizeBytes))
            {
                AddTargetGroup(new CleanupTargetGroupViewModel(target));
            }

            if (Targets.Count > 0)
            {
                SelectedTarget = Targets[0];
            }

            var status = Targets.Count == 0
                ? "No cleanup targets detected."
                : $"Preview ready: {SummaryText}";

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
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        if (IsBusy)
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

        var totalSizeMb = itemsToDelete.Sum(static tuple => tuple.item.SizeMegabytes);

        if (ConfirmDeletion is not null)
        {
            var confirmation = new CleanupDeletionConfirmation(itemsToDelete.Count, totalSizeMb);
            if (!ConfirmDeletion.Invoke(confirmation))
            {
                _mainViewModel.SetStatusMessage("Deletion cancelled by user.");
                return;
            }
        }

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

        try
        {
            IsDeleting = true;
            IsBusy = true;
            DeletionProgressTotal = itemsToDelete.Count;
            DeletionProgressCurrent = 0;
            DeletionStatusMessage = totalSizeMb > 0
                ? $"Removing {itemsToDelete.Count:N0} item(s) • {totalSizeMb:F2} MB"
                : $"Removing {itemsToDelete.Count:N0} item(s)";

            var progress = new Progress<CleanupDeletionProgress>(report =>
            {
                DeletionProgressCurrent = report.Completed;
                DeletionProgressTotal = report.Total;
                DeletionStatusMessage = string.IsNullOrEmpty(report.CurrentPath)
                    ? $"Deleting {report.Completed} of {report.Total}"
                    : $"Deleting {report.Completed}/{report.Total}: {report.CurrentPath}";
            });

            var deletionResult = await _cleanupService.DeleteAsync(itemsToDelete.Select(tuple => tuple.item.Model), progress);

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
            _mainViewModel.SetStatusMessage(deletionResult.ToStatusMessage());
            DeletionStatusMessage = deletionResult.ToStatusMessage();
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
        }
    }

    private bool CanDeleteSelected() => !IsBusy && HasSelection;

    [RelayCommand(CanExecute = nameof(CanSelectAllCurrent))]
    private void SelectAllCurrent()
    {
        foreach (var item in FilteredItems)
        {
            item.IsSelected = true;
        }
    }

    private bool CanSelectAllCurrent() => FilteredItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearCurrentSelection))]
    private void ClearCurrentSelection()
    {
        foreach (var item in FilteredItems)
        {
            item.IsSelected = false;
        }
    }

    private bool CanClearCurrentSelection() => FilteredItems.Any(static item => item.IsSelected);

    partial void OnIsBusyChanged(bool value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetChanged(CleanupTargetGroupViewModel? oldValue, CleanupTargetGroupViewModel? newValue)
    {
        if (oldValue is not null)
        {
            foreach (var item in oldValue.Items)
            {
                item.IsSelected = false;
            }
        }

        if (newValue is null || newValue.Items.Count == 0)
        {
            RefreshFilteredItems();
            SelectAllCurrentCommand.NotifyCanExecuteChanged();
            ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
            return;
        }

        RefreshFilteredItems();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedItemKindChanged(CleanupItemKind value)
    {
        RefreshFilteredItems();
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

    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(SelectedItemSizeMegabytes));
        OnPropertyChanged(nameof(HasSelection));
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
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(HasFilteredResults));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void AddTargetGroup(CleanupTargetGroupViewModel group)
    {
        group.SelectionChanged += OnGroupSelectionChanged;
        group.ItemsChanged += OnGroupItemsChanged;
        Targets.Add(group);
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(SummaryText));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
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
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
    }

    private void RefreshFilteredItems()
    {
        FilteredItems.Clear();

        if (SelectedTarget is null)
        {
            OnPropertyChanged(nameof(HasFilteredResults));
            OnPropertyChanged(nameof(ExtensionStatusText));
            SelectAllCurrentCommand.NotifyCanExecuteChanged();
            ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
            return;
        }

        foreach (var item in SelectedTarget.Items)
        {
            if (MatchesFilters(item))
            {
                FilteredItems.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasFilteredResults));
        OnPropertyChanged(nameof(ExtensionStatusText));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
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
