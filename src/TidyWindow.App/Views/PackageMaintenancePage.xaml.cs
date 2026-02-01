using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using WpfListView = System.Windows.Controls.ListView;
using WpfNavigationService = System.Windows.Navigation.NavigationService;

namespace TidyWindow.App.Views;

public partial class PackageMaintenancePage : Page, INavigationAware
{
    private readonly PackageMaintenanceViewModel _viewModel;
    private WpfListView? _packagesListView;
    private bool _disposed;
    private readonly Controls.PackageMaintenancePivotTitleBar _titleBar;
    private MainViewModel? _shellViewModel;
    private WpfNavigationService? _navigationService;

    public PackageMaintenancePage(PackageMaintenanceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _titleBar = new Controls.PackageMaintenancePivotTitleBar { DataContext = _viewModel };
        DataContext = _viewModel;

        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        _viewModel.PageChanged += OnPageChanged;
        Unloaded += OnPageUnloaded;
        Loaded += OnPageLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        AttachTitleBar();
        EnsureScrollHandlers();
        if (_viewModel.HasLoadedInitialData)
            return;
        if (_viewModel.RefreshCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private bool ConfirmElevation(string message)
    {
        var prompt = string.IsNullOrWhiteSpace(message)
            ? "This operation requires administrator privileges. Restart as administrator to continue?"
            : message;

        var result = MessageBox.Show(prompt,
            "Administrator privileges needed",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        return result == MessageBoxResult.Yes;
    }

    private void OnAdministratorRestartRequested(object? sender, EventArgs e)
    {
        var app = WpfApplication.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        if (ContentScrollViewer is null)
        {
            return;
        }

        ContentScrollViewer.ScrollToVerticalOffset(0);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // For cached pages, only clear the title bar but keep event subscriptions
        // This allows the page to restore properly when navigating back
        _shellViewModel?.SetTitleBarContent(null);

        // Keep event subscriptions and scroll handlers for cached pages
        // They will be restored via OnNavigatedTo/RestoreViewModelBindings
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _disposed)
        {
            RestoreViewModelBindings();
        }

        if (IsVisible)
        {
            AttachTitleBar();
        }
    }

    private void RestoreViewModelBindings()
    {
        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        _viewModel.PageChanged += OnPageChanged;
        if (_packagesListView is not null)
        {
            _packagesListView.Loaded += PackagesListView_Loaded;
        }

        Unloaded += OnPageUnloaded;
        _disposed = false;
        EnsureScrollHandlers();
        AttachTitleBar();
    }

    private void AttachTitleBar()
    {
        // Always refresh the shell view model reference to ensure we have the current one
        _shellViewModel = WpfApplication.Current?.MainWindow?.DataContext as MainViewModel;
        _shellViewModel?.SetTitleBarContent(_titleBar);

        _navigationService ??= WpfNavigationService.GetNavigationService(this);
        if (_navigationService is not null)
        {
            _navigationService.Navigated -= OnNavigated;
            _navigationService.Navigated += OnNavigated;
        }
    }

    private void OnNavigated(object? sender, NavigationEventArgs e)
    {
        if (ReferenceEquals(e.Content, this))
        {
            _shellViewModel?.SetTitleBarContent(_titleBar);
        }
        else
        {
            _shellViewModel?.SetTitleBarContent(null);
        }
    }

    private void PackagesListView_Loaded(object sender, RoutedEventArgs e)
    {
        _packagesListView ??= sender as WpfListView;
        if (_packagesListView is null)
        {
            return;
        }
        EnsureScrollHandlers();
    }


    private void EnsureScrollHandlers()
    {
        AttachScrollHandler(_packagesListView ?? PackagesListView);
        AttachScrollHandler(OperationsListView);
    }

    private static void AttachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
        control.PreviewMouseWheel += OnNestedPreviewMouseWheel;
    }

    private void DetachScrollHandlers()
    {
        DetachScrollHandler(_packagesListView ?? PackagesListView);
        DetachScrollHandler(OperationsListView);
    }

    private static void DetachScrollHandler(ItemsControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.PreviewMouseWheel -= OnNestedPreviewMouseWheel;
    }

    private void BubbleScroll(MouseWheelEventArgs e, DependencyObject source)
    {
        if (ContentScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var nestedScrollViewer = FindDescendant<ScrollViewer>(source);
        if (nestedScrollViewer is null)
        {
            return;
        }

        var canScrollUp = e.Delta > 0 && nestedScrollViewer.VerticalOffset > 0;
        var canScrollDown = e.Delta < 0 && nestedScrollViewer.VerticalOffset < nestedScrollViewer.ScrollableHeight;

        if (canScrollUp || canScrollDown)
        {
            return;
        }

        e.Handled = true;

        var targetOffset = ContentScrollViewer.VerticalOffset - e.Delta;
        if (targetOffset < 0)
        {
            targetOffset = 0;
        }
        else if (targetOffset > ContentScrollViewer.ScrollableHeight)
        {
            targetOffset = ContentScrollViewer.ScrollableHeight;
        }

        ContentScrollViewer.ScrollToVerticalOffset(targetOffset);
    }

    private static void OnNestedPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        if (FindParentPage(dependencyObject) is PackageMaintenancePage page)
        {
            page.BubbleScroll(e, dependencyObject);
        }
    }

    private static PackageMaintenancePage? FindParentPage(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is PackageMaintenancePage page)
            {
                return page;
            }

            node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        // Re-subscribe to events and ensure scroll handlers are attached
        if (_disposed)
        {
            RestoreViewModelBindings();
        }

        _viewModel.PageChanged -= OnPageChanged;
        _viewModel.PageChanged += OnPageChanged;

        AttachTitleBar();
        EnsureScrollHandlers();
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        // Clear title bar content
        _shellViewModel?.SetTitleBarContent(null);
        DetachScrollHandlers();
    }
}
