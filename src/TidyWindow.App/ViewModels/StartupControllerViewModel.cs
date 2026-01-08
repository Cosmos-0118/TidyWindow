using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Startup;

namespace TidyWindow.App.ViewModels;

public sealed partial class StartupEntryItemViewModel : ObservableObject
{
    public StartupEntryItemViewModel(StartupItem item)
    {
        UpdateFrom(item ?? throw new ArgumentNullException(nameof(item)));
    }

    public StartupItem Item { get; private set; } = null!;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string? publisher;

    [ObservableProperty]
    private StartupImpact impact;

    [ObservableProperty]
    private string source = string.Empty;

    [ObservableProperty]
    private string userContext = string.Empty;

    [ObservableProperty]
    private string lastModifiedDisplay = string.Empty;

    [ObservableProperty]
    private bool canDelay;

    public void UpdateFrom(StartupItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        IsEnabled = item.IsEnabled;
        Name = item.Name;
        Publisher = item.Publisher;
        Impact = item.Impact;
        Source = string.IsNullOrWhiteSpace(item.SourceTag)
            ? item.SourceKind.ToString()
            : item.SourceTag;
        UserContext = string.IsNullOrWhiteSpace(item.UserContext)
            ? "User"
            : item.UserContext;
        LastModifiedDisplay = FormatLastModified(item.LastModifiedUtc);
        CanDelay = ComputeCanDelay(item);
    }

    private static bool ComputeCanDelay(StartupItem item)
    {
        if (item.SourceKind is StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce or StartupItemSourceKind.StartupFolder)
        {
            return string.Equals(item.UserContext, "CurrentUser", StringComparison.OrdinalIgnoreCase) || (item.EntryLocation?.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) == true);
        }

        return false;
    }

    private static string FormatLastModified(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "Modified: unknown";
        }

        var local = timestamp.Value.ToLocalTime();
        return $"Modified: {local:yyyy-MM-dd HH:mm}";
    }
}

public sealed partial class StartupControllerViewModel : ObservableObject
{
    private readonly StartupInventoryService _inventory;
    private readonly StartupControlService _control;
    private readonly StartupDelayService _delay;
    private readonly ActivityLogService _activityLog;
    private readonly List<StartupEntryItemViewModel> _filteredEntries = new();

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private ObservableCollection<StartupEntryItemViewModel> entries = new();

    public ObservableCollection<StartupEntryItemViewModel> PagedEntries { get; } = new();

    [ObservableProperty]
    private int visibleCount;

    [ObservableProperty]
    private int disabledVisibleCount;

    [ObservableProperty]
    private int enabledVisibleCount;

    [ObservableProperty]
    private int unsignedVisibleCount;

    [ObservableProperty]
    private int highImpactVisibleCount;

    [ObservableProperty]
    private int baselineDisabledCount;

    private readonly int _pageSize = 24;
    private int _currentPage = 1;

    private const int DefaultDelaySeconds = 45;
    private static readonly TimeSpan MinimumBusyDuration = TimeSpan.FromMilliseconds(1000);

    public StartupControllerViewModel(StartupInventoryService inventory, StartupControlService control, StartupDelayService delay, ActivityLogService activityLog)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ToggleCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(ToggleAsync, CanToggle);
        EnableCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(EnableAsync, CanEnable);
        DisableCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DisableAsync, CanDisable);
        DelayCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DelayAsync, CanDelay);
    }

    public int CurrentPage => _currentPage;

    public int TotalPages => ComputeTotalPages(_filteredEntries.Count, _pageSize);

    public string PageDisplay => _filteredEntries.Count == 0
        ? "Page 0 of 0"
        : $"Page {_currentPage} of {TotalPages}";

    public bool CanGoToPreviousPage => _currentPage > 1;

    public bool CanGoToNextPage => _currentPage < TotalPages;

    public bool HasMultiplePages => _filteredEntries.Count > _pageSize;

    public event EventHandler? PageChanged;

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> ToggleCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> EnableCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> DisableCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> DelayCommand { get; }

    [RelayCommand]
    private void GoToPreviousPage()
    {
        if (!CanGoToPreviousPage)
        {
            return;
        }

        _currentPage--;
        RefreshPagedEntries(raisePageChanged: true);
    }

    [RelayCommand]
    private void GoToNextPage()
    {
        if (!CanGoToNextPage)
        {
            return;
        }

        _currentPage++;
        RefreshPagedEntries(raisePageChanged: true);
    }

    private bool CanToggle(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy;

    private bool CanEnable(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy && !item.IsEnabled;

    private bool CanDisable(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy && item.IsEnabled;

    partial void OnIsBusyChanged(bool value)
    {
        ToggleCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        DelayCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAsync()
    {
        var busyStartedAt = DateTime.UtcNow;
        IsBusy = true;
        try
        {
            await Task.Yield(); // Let the UI paint the busy indicator before heavy work begins.

            var snapshot = await _inventory.GetInventoryAsync();
            var mapped = snapshot.Items.Select(startup => new StartupEntryItemViewModel(startup)).ToList();
            Entries = new ObservableCollection<StartupEntryItemViewModel>(mapped);
            BaselineDisabledCount = mapped.Count(item => !item.IsEnabled);
        }
        finally
        {
            var remaining = MinimumBusyDuration - (DateTime.UtcNow - busyStartedAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining); // Keep the busy overlay visible long enough to read.
            }
            IsBusy = false;
        }
    }

    private async Task ToggleAsync(StartupEntryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            StartupToggleResult result;
            if (item.IsEnabled)
            {
                result = await _control.DisableAsync(item.Item);
            }
            else
            {
                result = await _control.EnableAsync(item.Item);
            }

            if (result.Succeeded)
            {
                item.UpdateFrom(result.Item);
                _activityLog.LogSuccess("StartupController", $"{(result.Item.IsEnabled ? "Enabled" : "Disabled")} {result.Item.Name}", new object?[] { result.Item.EntryLocation, result.Backup?.CreatedAtUtc });
            }
            else
            {
                _activityLog.LogWarning("StartupController", $"Failed to toggle {item.Name}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError("StartupController", $"Error toggling {item.Name}: {ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task EnableAsync(StartupEntryItemViewModel? item)
    {
        if (item is null || item.IsEnabled)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var result = await _control.EnableAsync(item.Item);
            if (result.Succeeded)
            {
                item.UpdateFrom(result.Item);
                _activityLog.LogSuccess("StartupController", $"Enabled {result.Item.Name}", new object?[] { result.Item.EntryLocation, result.Backup?.CreatedAtUtc });
            }
            else
            {
                _activityLog.LogWarning("StartupController", $"Failed to enable {item.Name}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError("StartupController", $"Error enabling {item.Name}: {ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task DisableAsync(StartupEntryItemViewModel? item)
    {
        if (item is null || !item.IsEnabled)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var result = await _control.DisableAsync(item.Item);
            if (result.Succeeded)
            {
                item.UpdateFrom(result.Item);
                _activityLog.LogSuccess("StartupController", $"Disabled {result.Item.Name}", new object?[] { result.Item.EntryLocation, result.Backup?.CreatedAtUtc });
            }
            else
            {
                _activityLog.LogWarning("StartupController", $"Failed to disable {item.Name}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError("StartupController", $"Error disabling {item.Name}: {ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private bool CanDelay(StartupEntryItemViewModel? item) => item is not null && item.CanDelay && !IsBusy && !item.IsBusy;

    private async Task DelayAsync(StartupEntryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var delaySeconds = DefaultDelaySeconds;
            var result = await _delay.DelayAsync(item.Item, TimeSpan.FromSeconds(delaySeconds));
            if (result.Succeeded)
            {
                _activityLog.LogSuccess("StartupController", $"Delayed {item.Name} by {delaySeconds}s", new object?[] { result.ReplacementTaskPath });
                await RefreshAsync();
            }
            else
            {
                _activityLog.LogWarning("StartupController", $"Failed to delay {item.Name}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _activityLog.LogError("StartupController", $"Error delaying {item.Name}: {ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public void ApplyVisibleEntries(IReadOnlyList<StartupEntryItemViewModel> visibleEntries, bool resetPage)
    {
        _filteredEntries.Clear();

        if (visibleEntries is not null)
        {
            _filteredEntries.AddRange(visibleEntries);
        }

        if (resetPage)
        {
            ResetToFirstPage();
        }

        RefreshPagedEntries(raisePageChanged: resetPage);
        RefreshVisibleCounters();
        RefreshCommandStates();
    }

    public void RefreshVisibleCounters()
    {
        UpdateCounters(_filteredEntries);
    }

    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        DelayCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPagedEntries(bool raisePageChanged)
    {
        var totalPages = TotalPages;
        if (_currentPage > totalPages)
        {
            _currentPage = totalPages;
        }

        var skip = (_currentPage - 1) * _pageSize;
        var pageItems = _filteredEntries
            .Skip(skip)
            .Take(_pageSize)
            .ToList();

        PagedEntries.Clear();
        foreach (var item in pageItems)
        {
            PagedEntries.Add(item);
        }

        RaisePagingProperties();

        if (raisePageChanged)
        {
            PageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateCounters(IReadOnlyList<StartupEntryItemViewModel> items)
    {
        var list = items ?? Array.Empty<StartupEntryItemViewModel>();

        VisibleCount = list.Count;
        DisabledVisibleCount = list.Count(item => !item.IsEnabled);
        EnabledVisibleCount = list.Count(item => item.IsEnabled);
        UnsignedVisibleCount = list.Count(item => item.Item.SignatureStatus == StartupSignatureStatus.Unsigned);
        HighImpactVisibleCount = list.Count(item => item.Impact == StartupImpact.High);
    }

    private void ResetToFirstPage()
    {
        _currentPage = 1;
    }

    private void RaisePagingProperties()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(HasMultiplePages));
    }

    private static int ComputeTotalPages(int itemCount, int pageSize)
    {
        if (itemCount <= 0)
        {
            return 1;
        }

        var sanitizedPageSize = Math.Max(1, pageSize);
        return (itemCount + sanitizedPageSize - 1) / sanitizedPageSize;
    }
}
