using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Cleanup;

namespace TidyWindow.App.ViewModels;

public sealed partial class CleanupViewModel : ViewModelBase
{
    private readonly CleanupService _cleanupService;
    private readonly MainViewModel _mainViewModel;

    private int _previewCount = 10;

    [ObservableProperty]
    private bool _includeDownloads = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _headline = "Preview and clean up system clutter";

    public CleanupViewModel(CleanupService cleanupService, MainViewModel mainViewModel)
    {
        _cleanupService = cleanupService;
        _mainViewModel = mainViewModel;

    }

    public ObservableCollection<CleanupTargetGroupViewModel> Targets { get; } = new();

    [ObservableProperty]
    private CleanupTargetGroupViewModel? _selectedTarget;

    public int PreviewCount
    {
        get => _previewCount;
        set
        {
            var sanitized = value < 0 ? 0 : value;
            SetProperty(ref _previewCount, sanitized);
        }
    }

    public bool HasResults => Targets.Count > 0;

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

            var report = await _cleanupService.PreviewAsync(IncludeDownloads, PreviewCount);
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

        try
        {
            IsBusy = true;

            var deletionResult = await _cleanupService.DeleteAsync(itemsToDelete.Select(tuple => tuple.item.Model));

            foreach (var (group, item) in itemsToDelete)
            {
                group.Items.Remove(item);
            }

            foreach (var emptyGroup in Targets.Where(static group => group.RemainingItemCount == 0).ToList())
            {
                RemoveTargetGroup(emptyGroup);
            }

            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(SelectedItemCount));
            OnPropertyChanged(nameof(SelectedItemSizeMegabytes));
            OnPropertyChanged(nameof(HasSelection));
            _mainViewModel.SetStatusMessage(deletionResult.ToStatusMessage());
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Delete failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            DeleteSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDeleteSelected() => !IsBusy && HasSelection;

    [RelayCommand(CanExecute = nameof(CanSelectAllCurrent))]
    private void SelectAllCurrent()
    {
        if (SelectedTarget is null)
        {
            return;
        }

        foreach (var item in SelectedTarget.Items)
        {
            item.IsSelected = true;
        }
    }

    private bool CanSelectAllCurrent() => SelectedTarget is not null && SelectedTarget.Items.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearCurrentSelection))]
    private void ClearCurrentSelection()
    {
        if (SelectedTarget is null)
        {
            return;
        }

        foreach (var item in SelectedTarget.Items)
        {
            item.IsSelected = false;
        }
    }

    private bool CanClearCurrentSelection() => SelectedTarget is not null && SelectedTarget.SelectedCount > 0;

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
            SelectAllCurrentCommand.NotifyCanExecuteChanged();
            ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
            return;
        }

        // Keep current selection state when switching between groups.
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnGroupSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(SelectedItemSizeMegabytes));
        OnPropertyChanged(nameof(HasSelection));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnGroupItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(SelectedItemSizeMegabytes));
        OnPropertyChanged(nameof(HasSelection));
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
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(SummaryText));
        SelectAllCurrentCommand.NotifyCanExecuteChanged();
        ClearCurrentSelectionCommand.NotifyCanExecuteChanged();
    }
}
