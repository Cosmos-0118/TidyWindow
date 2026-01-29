using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class EssentialsPage : Page
{
    private readonly EssentialsViewModel _viewModel;
    private readonly Controls.EssentialsPivotTitleBar _titleBar;
    private bool _disposed;
    private readonly bool _shouldDisposeOnUnload;
    private MainViewModel? _shellViewModel;
    private System.Windows.Navigation.NavigationService? _navigationService;

    public EssentialsPage(EssentialsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _titleBar = new Controls.EssentialsPivotTitleBar { DataContext = _viewModel };
        DataContext = viewModel;
        _shouldDisposeOnUnload = !PageCacheRegistry.IsCacheable(GetType());
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        AttachTitleBar();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            AttachTitleBar();
        }
    }

    private void AttachTitleBar()
    {
        _shellViewModel ??= System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel;
        _shellViewModel?.SetTitleBarContent(_titleBar);

        _navigationService ??= System.Windows.Navigation.NavigationService.GetNavigationService(this);
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
        else if (_shellViewModel is not null)
        {
            _shellViewModel.SetTitleBarContent(null);
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

}
