using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Startup;

namespace TidyWindow.App.Views;

public partial class StartupControllerPage : Page, INavigationAware
{
    private readonly StartupControllerViewModel _viewModel;
    private readonly CollectionViewSource _entriesView;
    private INotifyCollectionChanged? _entriesNotifier;
    private ScrollViewer? _entriesScrollViewer;
    private CancellationTokenSource? _searchDebounceCts;
    private readonly Dictionary<string, CancellationTokenSource> _pendingStatusFilterExitTokens = new(StringComparer.OrdinalIgnoreCase);
    private bool _isSubscriptionsAttached;
    private bool _isRollingBackGuardToggle;

    private bool _includeRun = true;
    private bool _includeStartup = true;
    private bool _includeTasks = true;
    private bool _includeServices = true;
    private bool _filterSafe;
    private bool _includeSystemCritical;
    private bool _filterUnsigned;
    private bool _filterHighImpact;
    private bool _showEnabled = true;
    private bool _showDisabled = true;
    private bool _showBackupOnly;
    private string _search = string.Empty;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StatusFilterExitDelay = TimeSpan.FromMilliseconds(900);

    public StartupControllerPage(StartupControllerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        _entriesView = (CollectionViewSource)FindResource("StartupEntriesView");
        Loaded += OnLoaded;
        AttachViewModelSubscriptions();
        Unloaded += OnUnloaded;
    }

    // Test-only constructor to bypass XAML initialization.
    internal StartupControllerPage(bool skipInitializeComponent)
    {
        if (!skipInitializeComponent)
        {
            throw new ArgumentException("Use the public constructor in production code.", nameof(skipInitializeComponent));
        }

        var preferences = new UserPreferencesService();
        _viewModel = new StartupControllerViewModel(
            new StartupInventoryService(),
            new StartupControlService(),
            new StartupDelayService(),
            new ActivityLogService(),
            preferences,
            new UserConfirmationService(),
            new StartupGuardService());

        _entriesView = new CollectionViewSource();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        RefreshView(resetPage: true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(StartupControllerViewModel.Entries), StringComparison.Ordinal))
        {
            SubscribeToEntries();
            RefreshView(resetPage: true);
        }
    }

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();

        _searchDebounceCts = new CancellationTokenSource();
        var token = _searchDebounceCts.Token;

        try
        {
            await Task.Delay(SearchDebounceDelay, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        _search = SearchBox.Text?.Trim() ?? string.Empty;
        RefreshView(resetPage: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelPendingSearch();
        DetachViewModelSubscriptions();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_entriesView is null)
        {
            return; // Ignore early filter events during construction.
        }

        SyncFilterToggles();
        ReconcilePendingStatusFilterExits();
        RefreshView(resetPage: true);
    }

    private void SyncFilterToggles()
    {
        _includeRun = RunFilter?.IsChecked == true;
        _includeStartup = StartupFilter?.IsChecked == true;
        _includeTasks = TasksFilter?.IsChecked == true;
        _includeServices = ServicesFilter?.IsChecked == true;
        _filterSafe = SafeFilter?.IsChecked == true;
        _includeSystemCritical = SystemFilter?.IsChecked == true;
        _filterUnsigned = UnsignedFilter?.IsChecked == true;
        _filterHighImpact = HighImpactFilter?.IsChecked == true;
        _showEnabled = ShowEnabledFilter?.IsChecked != false; // default true
        _showDisabled = ShowDisabledFilter?.IsChecked != false; // default true
        _showBackupOnly = BackupFilter?.IsChecked == true;
    }

    private void RefreshView(bool resetPage)
    {
        SyncFilterToggles();
        ReconcilePendingStatusFilterExits();

        if (_entriesView.View is null)
        {
            _viewModel.ApplyVisibleEntries(Array.Empty<StartupEntryItemViewModel>(), resetPage);
            return;
        }

        _entriesView.View.Refresh();
        var filteredItems = _entriesView.View.Cast<StartupEntryItemViewModel>().ToList();

        // When backup filter is active, merge in backup-only entries with deduplication
        if (_showBackupOnly)
        {
            var existingIds = new HashSet<string>(filteredItems.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
            var backupEntries = _viewModel.BackupOnlyEntries
                .Where(b => !existingIds.Contains(b.Item.Id))
                .Where(PassesFilters)
                .ToList();
            filteredItems.AddRange(backupEntries);
        }

        _viewModel.ApplyVisibleEntries(filteredItems, resetPage);
    }

    private void SubscribeToEntries()
    {
        if (_entriesNotifier is not null)
        {
            _entriesNotifier.CollectionChanged -= OnEntriesCollectionChanged;
            UnsubscribeFromEntryChanges(_entriesNotifier as IEnumerable<StartupEntryItemViewModel>);
        }

        if (_viewModel.Entries is INotifyCollectionChanged notifier)
        {
            _entriesNotifier = notifier;
            notifier.CollectionChanged += OnEntriesCollectionChanged;
        }

        SubscribeToEntryChanges(_viewModel.Entries);
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SubscribeToEntryChanges(e.NewItems?.OfType<StartupEntryItemViewModel>() ?? Enumerable.Empty<StartupEntryItemViewModel>());
        UnsubscribeFromEntryChanges(e.OldItems?.OfType<StartupEntryItemViewModel>() ?? Enumerable.Empty<StartupEntryItemViewModel>());
        RefreshView(resetPage: true);
    }

    private void SubscribeToEntryChanges(IEnumerable<StartupEntryItemViewModel> items)
    {
        foreach (var item in items)
        {
            item.PropertyChanged += OnEntryPropertyChanged;
        }
    }

    private void UnsubscribeFromEntryChanges(IEnumerable<StartupEntryItemViewModel>? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            item.PropertyChanged -= OnEntryPropertyChanged;
            CancelPendingStatusFilterExit(item);
        }
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var isEnabledChange = string.Equals(e.PropertyName, nameof(StartupEntryItemViewModel.IsEnabled), StringComparison.Ordinal);
        var isBusyChange = string.Equals(e.PropertyName, nameof(StartupEntryItemViewModel.IsBusy), StringComparison.Ordinal);

        if (isEnabledChange)
        {
            if (sender is StartupEntryItemViewModel entry)
            {
                HandleStatusFilterTransition(entry);
            }

            RefreshView(resetPage: false); // Re-apply filters when enable/disable toggled.
        }

        if (isBusyChange)
        {
            _viewModel.RefreshVisibleCounters();
        }

        _viewModel.RefreshCommandStates();
    }

    private void OnEntriesFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not StartupEntryItemViewModel entry)
        {
            e.Accepted = false;
            return;
        }

        e.Accepted = PassesFilters(entry);
    }

    private bool PassesFilters(StartupEntryItemViewModel entry)
    {
        return PassesFilters(entry, ignoreStatusFilters: false, honorPendingFilterExit: true);
    }

    private bool PassesFilters(StartupEntryItemViewModel entry, bool ignoreStatusFilters, bool honorPendingFilterExit)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Group Run keys and similar registry-based startup types together
        var isRunType = entry.Item.SourceKind is StartupItemSourceKind.RunKey
            or StartupItemSourceKind.RunOnce
            or StartupItemSourceKind.Winlogon
            or StartupItemSourceKind.ActiveSetup
            or StartupItemSourceKind.ExplorerRun
            or StartupItemSourceKind.AppInitDll
            or StartupItemSourceKind.ImageFileExecutionOptions
            or StartupItemSourceKind.BootExecute
            or StartupItemSourceKind.ShellFolder;

        if (!_includeRun && isRunType)
        {
            return false;
        }

        if (!_includeStartup && entry.Item.SourceKind == StartupItemSourceKind.StartupFolder)
        {
            return false;
        }

        if (!_includeTasks && entry.Item.SourceKind == StartupItemSourceKind.ScheduledTask)
        {
            return false;
        }

        if (!_includeServices && entry.Item.SourceKind == StartupItemSourceKind.Service)
        {
            return false;
        }

        var isSystem = IsSystem(entry);
        if (!_includeSystemCritical && isSystem)
        {
            return false;
        }

        if (_filterSafe || _filterUnsigned || _filterHighImpact)
        {
            var matchesSafe = _filterSafe && IsSafe(entry);
            var matchesUnsigned = _filterUnsigned && entry.Item.SignatureStatus == StartupSignatureStatus.Unsigned;
            var matchesHigh = _filterHighImpact && entry.Impact == StartupImpact.High;

            if (!matchesSafe && !matchesUnsigned && !matchesHigh)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(_search))
        {
            if (ignoreStatusFilters)
            {
                return true;
            }

            if (honorPendingFilterExit && entry.IsPendingFilterExit && IsStatusFilteredOut(entry))
            {
                return true;
            }

            return !IsStatusFilteredOut(entry);
        }

        var term = _search;
        var matchesSearch = ContainsOrdinalIgnoreCase(entry.Name, term)
            || ContainsOrdinalIgnoreCase(entry.Publisher, term)
            || ContainsOrdinalIgnoreCase(entry.Item.ExecutablePath, term)
            || ContainsOrdinalIgnoreCase(entry.Item.EntryLocation, term);

        if (!matchesSearch)
        {
            return false;
        }

        if (ignoreStatusFilters)
        {
            return true;
        }

        if (honorPendingFilterExit && entry.IsPendingFilterExit && IsStatusFilteredOut(entry))
        {
            return true;
        }

        return !IsStatusFilteredOut(entry);
    }

    private bool IsStatusFilteredOut(StartupEntryItemViewModel entry)
    {
        return (!_showEnabled && entry.IsEnabled)
            || (!_showDisabled && !entry.IsEnabled);
    }

    private void HandleStatusFilterTransition(StartupEntryItemViewModel entry)
    {
        CancelPendingStatusFilterExit(entry);

        var shouldDelayRemoval = IsStatusFilteredOut(entry)
            && PassesFilters(entry, ignoreStatusFilters: true, honorPendingFilterExit: false);

        if (!shouldDelayRemoval)
        {
            entry.IsPendingFilterExit = false;
            return;
        }

        entry.IsPendingFilterExit = true;

        var cts = new CancellationTokenSource();
        _pendingStatusFilterExitTokens[entry.Item.Id] = cts;
        _ = RemoveAfterStatusFilterDelayAsync(entry, cts);
    }

    private async Task RemoveAfterStatusFilterDelayAsync(StartupEntryItemViewModel entry, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(StatusFilterExitDelay, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_pendingStatusFilterExitTokens.TryGetValue(entry.Item.Id, out var activeToken)
            || !ReferenceEquals(activeToken, cts))
        {
            return;
        }

        _pendingStatusFilterExitTokens.Remove(entry.Item.Id);
        entry.IsPendingFilterExit = false;
        cts.Dispose();
        RefreshView(resetPage: false);
    }

    private void ReconcilePendingStatusFilterExits()
    {
        var pendingEntries = _viewModel.Entries
            .Where(entry => entry.IsPendingFilterExit)
            .ToList();

        foreach (var entry in pendingEntries)
        {
            var shouldRemainPending = IsStatusFilteredOut(entry)
                && PassesFilters(entry, ignoreStatusFilters: true, honorPendingFilterExit: false);

            if (!shouldRemainPending)
            {
                CancelPendingStatusFilterExit(entry);
            }
        }
    }

    private void CancelPendingStatusFilterExit(StartupEntryItemViewModel entry)
    {
        if (_pendingStatusFilterExitTokens.TryGetValue(entry.Item.Id, out var cts))
        {
            _pendingStatusFilterExitTokens.Remove(entry.Item.Id);
            cts.Cancel();
            cts.Dispose();
        }

        entry.IsPendingFilterExit = false;
    }

    private void CancelAllPendingStatusFilterExits()
    {
        foreach (var cts in _pendingStatusFilterExitTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _pendingStatusFilterExitTokens.Clear();

        foreach (var entry in _viewModel.Entries)
        {
            entry.IsPendingFilterExit = false;
        }
    }

    private static bool ContainsOrdinalIgnoreCase(string? source, string term)
    {
        return !string.IsNullOrEmpty(source)
            && source.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsSafe(StartupEntryItemViewModel entry)
    {
        return StartupSafetyClassifier.IsSafeToDisable(entry.Item);
    }

    private static bool IsSystem(StartupEntryItemViewModel entry)
    {
        return StartupSafetyClassifier.IsSystemCritical(entry.Item);
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        ScrollEntriesToTop();
    }

    private void OnEntriesLoaded(object sender, RoutedEventArgs e)
    {
        _entriesScrollViewer ??= FindScrollViewer(EntriesItemsControl);
    }

    private void ScrollEntriesToTop()
    {
        // Run immediately for already-realized viewers, and schedule a follow-up once layout settles.
        ScrollToTopInternal();
        Dispatcher.BeginInvoke(ScrollToTopInternal, DispatcherPriority.Render);
    }

    private void ScrollToTopInternal()
    {
        _entriesScrollViewer = FindScrollViewer(EntriesItemsControl);
        _entriesScrollViewer?.ScrollToVerticalOffset(0);
    }

    private async void OnGuardToggled(object sender, RoutedEventArgs e)
    {
        if (_isRollingBackGuardToggle)
        {
            return;
        }

        if (sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: StartupEntryItemViewModel entry } toggle)
        {
            return;
        }

        var isChecked = toggle.IsChecked == true;
        try
        {
            await _viewModel.SetGuardAsync(entry, isChecked).ConfigureAwait(true);
        }
        catch
        {
            _isRollingBackGuardToggle = true;
            try
            {
                var previousValue = !isChecked;
                toggle.IsChecked = previousValue;
                entry.IsAutoGuardEnabled = previousValue;
            }
            finally
            {
                _isRollingBackGuardToggle = false;
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        // Clear cached scroll viewer reference to force re-discovery
        // This fixes scroll-to-top not working after page is cached
        _entriesScrollViewer = null;

        AttachViewModelSubscriptions();
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        CancelPendingSearch();
        DetachViewModelSubscriptions();

        // Clear scroll viewer reference when navigating away
        _entriesScrollViewer = null;
    }

    private void AttachViewModelSubscriptions()
    {
        if (_isSubscriptionsAttached)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.PageChanged += OnPageChanged;
        SubscribeToEntries();
        _isSubscriptionsAttached = true;
    }

    private void DetachViewModelSubscriptions()
    {
        if (!_isSubscriptionsAttached)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PageChanged -= OnPageChanged;

        if (_entriesNotifier is not null)
        {
            _entriesNotifier.CollectionChanged -= OnEntriesCollectionChanged;
            UnsubscribeFromEntryChanges(_entriesNotifier as IEnumerable<StartupEntryItemViewModel>);
            _entriesNotifier = null;
        }

        UnsubscribeFromEntryChanges(_viewModel.Entries);
        CancelAllPendingStatusFilterExits();
        _isSubscriptionsAttached = false;
    }

    private void CancelPendingSearch()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;
    }
}
