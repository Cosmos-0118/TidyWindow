using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Startup;

namespace TidyWindow.App.Views;

public partial class StartupControllerPage : Page
{
    private readonly StartupControllerViewModel _viewModel;
    private readonly CollectionViewSource _entriesView;
    private INotifyCollectionChanged? _entriesNotifier;
    private ScrollViewer? _entriesScrollViewer;

    private bool _includeRun = true;
    private bool _includeStartup = true;
    private bool _includeTasks = true;
    private bool _includeServices = true;
    private bool _filterSafe;
    private bool _filterUnsigned;
    private bool _filterHighImpact;
    private bool _showEnabled = true;
    private bool _showDisabled = true;
    private string _search = string.Empty;

    public StartupControllerPage(StartupControllerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        _entriesView = (CollectionViewSource)FindResource("StartupEntriesView");
        Loaded += OnLoaded;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.PageChanged += OnPageChanged;
        Unloaded += OnUnloaded;
    }

    // Test-only constructor to bypass XAML initialization.
    internal StartupControllerPage(bool skipInitializeComponent)
    {
        if (!skipInitializeComponent)
        {
            throw new ArgumentException("Use the public constructor in production code.", nameof(skipInitializeComponent));
        }

        _viewModel = new StartupControllerViewModel(
            new StartupInventoryService(),
            new StartupControlService(),
            new StartupDelayService(),
            new ActivityLogService());

        _entriesView = new CollectionViewSource();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        SubscribeToEntries();
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

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text?.Trim() ?? string.Empty;
        RefreshView(resetPage: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PageChanged -= OnPageChanged;

        if (_entriesNotifier is not null)
        {
            _entriesNotifier.CollectionChanged -= OnEntriesCollectionChanged;
            UnsubscribeFromEntryChanges(_entriesNotifier as IEnumerable<StartupEntryItemViewModel>);
            _entriesNotifier = null;
        }

        UnsubscribeFromEntryChanges(_viewModel.Entries);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_entriesView is null)
        {
            return; // Ignore early filter events during construction.
        }

        SyncFilterToggles();
        RefreshView(resetPage: true);
    }

    private void SyncFilterToggles()
    {
        _includeRun = RunFilter?.IsChecked == true;
        _includeStartup = StartupFilter?.IsChecked == true;
        _includeTasks = TasksFilter?.IsChecked == true;
        _includeServices = ServicesFilter?.IsChecked == true;
        _filterSafe = SafeFilter?.IsChecked == true;
        _filterUnsigned = UnsignedFilter?.IsChecked == true;
        _filterHighImpact = HighImpactFilter?.IsChecked == true;
        _showEnabled = ShowEnabledFilter?.IsChecked != false; // default true
        _showDisabled = ShowDisabledFilter?.IsChecked != false; // default true
    }

    private void RefreshView(bool resetPage)
    {
        SyncFilterToggles();

        if (_entriesView.View is null)
        {
            _viewModel.ApplyVisibleEntries(Array.Empty<StartupEntryItemViewModel>(), resetPage);
            return;
        }

        _entriesView.View.Refresh();
        var filteredItems = _entriesView.View.Cast<StartupEntryItemViewModel>().ToList();
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
        }
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var isEnabledChange = string.Equals(e.PropertyName, nameof(StartupEntryItemViewModel.IsEnabled), StringComparison.Ordinal);
        var isBusyChange = string.Equals(e.PropertyName, nameof(StartupEntryItemViewModel.IsBusy), StringComparison.Ordinal);

        if (isEnabledChange)
        {
            RefreshView(resetPage: false); // Re-apply filters when enable/disable toggled.
            return;
        }

        if (isBusyChange)
        {
            _viewModel.RefreshVisibleCounters();
        }
    }

    private void OnEntriesFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not StartupEntryItemViewModel entry)
        {
            e.Accepted = false;
            return;
        }

        if (!_includeRun && (entry.Item.SourceKind == StartupItemSourceKind.RunKey || entry.Item.SourceKind == StartupItemSourceKind.RunOnce))
        {
            e.Accepted = false;
            return;
        }

        if (!_includeStartup && entry.Item.SourceKind == StartupItemSourceKind.StartupFolder)
        {
            e.Accepted = false;
            return;
        }

        if (!_includeTasks && entry.Item.SourceKind == StartupItemSourceKind.ScheduledTask)
        {
            e.Accepted = false;
            return;
        }

        if (!_includeServices && entry.Item.SourceKind == StartupItemSourceKind.Service)
        {
            e.Accepted = false;
            return;
        }

        if (_filterSafe || _filterUnsigned || _filterHighImpact)
        {
            var matchesSafe = _filterSafe && IsSafe(entry);
            var matchesUnsigned = _filterUnsigned && entry.Item.SignatureStatus == StartupSignatureStatus.Unsigned;
            var matchesHigh = _filterHighImpact && entry.Impact == StartupImpact.High;

            if (!matchesSafe && !matchesUnsigned && !matchesHigh)
            {
                e.Accepted = false;
                return;
            }
        }

        if (!_showEnabled && entry.IsEnabled)
        {
            e.Accepted = false;
            return;
        }

        if (!_showDisabled && !entry.IsEnabled)
        {
            e.Accepted = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(_search))
        {
            e.Accepted = true;
            return;
        }

        var term = _search.ToLowerInvariant();
        var matches = (entry.Name ?? string.Empty).ToLowerInvariant().Contains(term)
                      || (entry.Publisher ?? string.Empty).ToLowerInvariant().Contains(term)
                      || (entry.Item.ExecutablePath ?? string.Empty).ToLowerInvariant().Contains(term)
                      || (entry.Item.EntryLocation ?? string.Empty).ToLowerInvariant().Contains(term);

        e.Accepted = matches;
    }

    private static bool IsSafe(StartupEntryItemViewModel entry)
    {
        if (entry.Item.SignatureStatus == StartupSignatureStatus.Unsigned)
        {
            return false;
        }

        if (entry.Item.SourceKind == StartupItemSourceKind.Service)
        {
            return false;
        }

        return entry.Impact != StartupImpact.High;
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        _entriesScrollViewer ??= FindScrollViewer(EntriesItemsControl);
        _entriesScrollViewer?.ScrollToVerticalOffset(0);
    }

    private void OnEntriesLoaded(object sender, RoutedEventArgs e)
    {
        _entriesScrollViewer ??= FindScrollViewer(EntriesItemsControl);
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
}
