using System;
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

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private ObservableCollection<StartupEntryItemViewModel> entries = new();

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

    private const int DefaultDelaySeconds = 45;

    public StartupControllerViewModel(StartupInventoryService inventory, StartupControlService control, StartupDelayService delay, ActivityLogService activityLog)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ToggleCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(ToggleAsync, CanToggle);
        DelayCommand = new AsyncRelayCommand<StartupEntryItemViewModel>(DelayAsync, CanDelay);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> ToggleCommand { get; }

    public IAsyncRelayCommand<StartupEntryItemViewModel> DelayCommand { get; }

    private bool CanToggle(StartupEntryItemViewModel? item) => item is not null && !IsBusy && !item.IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        ToggleCommand.NotifyCanExecuteChanged();
        DelayCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var snapshot = await _inventory.GetInventoryAsync().ConfigureAwait(false);
            var mapped = snapshot.Items.Select(startup => new StartupEntryItemViewModel(startup)).ToList();
            Entries = new ObservableCollection<StartupEntryItemViewModel>(mapped);
            BaselineDisabledCount = mapped.Count(item => !item.IsEnabled);
        }
        finally
        {
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
                result = await _control.DisableAsync(item.Item).ConfigureAwait(false);
            }
            else
            {
                result = await _control.EnableAsync(item.Item).ConfigureAwait(false);
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
            var result = await _delay.DelayAsync(item.Item, TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
            if (result.Succeeded)
            {
                _activityLog.LogSuccess("StartupController", $"Delayed {item.Name} by {delaySeconds}s", new object?[] { result.ReplacementTaskPath });
                await RefreshAsync().ConfigureAwait(false);
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
}
