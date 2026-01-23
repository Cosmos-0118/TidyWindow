using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using TidyWindow.App.ViewModels;

using WpfNavigationService = System.Windows.Navigation.NavigationService;

namespace TidyWindow.App.Views;

public partial class KnownProcessesPage : Page
{
    private readonly KnownProcessesViewModel _viewModel;
    private readonly Controls.KnownProcessesPivotTitleBar _titleBar;
    private MainViewModel? _shellViewModel;
    private WpfNavigationService? _navigationService;

    public KnownProcessesPage(KnownProcessesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _titleBar = new Controls.KnownProcessesPivotTitleBar { DataContext = _viewModel };
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        AttachTitleBar();
        _viewModel.EnsureInitialized();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_navigationService is not null)
        {
            _navigationService.Navigated -= OnNavigated;
        }

        _shellViewModel?.SetTitleBarContent(null);
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
}
