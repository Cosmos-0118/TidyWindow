using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

using WpfNavigationService = System.Windows.Navigation.NavigationService;

namespace TidyWindow.App.Views;

public partial class InstallHubPage : Page, INavigationAware
{
    private readonly InstallHubViewModel _viewModel;
    private readonly Controls.InstallHubPivotTitleBar _titleBar;
    private bool _disposed;
    private readonly bool _shouldDisposeOnUnload;
    private MainViewModel? _shellViewModel;
    private WpfNavigationService? _navigationService;

    public InstallHubPage(InstallHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _titleBar = new Controls.InstallHubPivotTitleBar { DataContext = _viewModel };
        DataContext = viewModel;
        _shouldDisposeOnUnload = !PageCacheRegistry.IsCacheable(GetType());

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        AttachTitleBar();
        await EnsureViewModelInitializedAsync();
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            AttachTitleBar();
            await EnsureViewModelInitializedAsync();
        }
    }

    private void AttachTitleBar()
    {
        _shellViewModel ??= System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel;
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

    private async Task EnsureViewModelInitializedAsync()
    {
        if (!_viewModel.IsInitialized)
        {
            await _viewModel.EnsureLoadedAsync();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_navigationService is not null)
        {
            _navigationService.Navigated -= OnNavigated;
        }

        if (_disposed || !_shouldDisposeOnUnload)
        {
            _shellViewModel?.SetTitleBarContent(null);
            return;
        }

        IsVisibleChanged -= OnIsVisibleChanged;
        Unloaded -= OnPageUnloaded;
        _shellViewModel?.SetTitleBarContent(null);
        _viewModel.Dispose();
        _disposed = true;
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        // Re-attach title bar when navigating back to this cached page
        AttachTitleBar();
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        // Clear title bar content when navigating away
        _shellViewModel?.SetTitleBarContent(null);
    }
}
