using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly NavigationService _navigationService;
    private readonly MainViewModel _viewModel;
    private System.Windows.Navigation.NavigationService? _frameNavigationService;
    private bool _initialNavigationCompleted;

    public MainWindow(MainViewModel viewModel, NavigationService navigationService)
    {
        InitializeComponent();
        _navigationService = navigationService;
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ToggleLoadingOverlay(_viewModel.IsShellLoading);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _viewModel.BeginShellLoad();
        _navigationService.Initialize(ContentFrame);
        _frameNavigationService = ContentFrame.NavigationService;
        if (_frameNavigationService is not null)
        {
            _frameNavigationService.LoadCompleted += OnInitialNavigationCompleted;
        }
        _viewModel.Activate();
    }

    private async void OnInitialNavigationCompleted(object? sender, System.Windows.Navigation.NavigationEventArgs e)
    {
        if (_initialNavigationCompleted)
        {
            return;
        }

        _initialNavigationCompleted = true;

        if (_frameNavigationService is not null)
        {
            _frameNavigationService.LoadCompleted -= OnInitialNavigationCompleted;
        }

        await Task.Delay(250);
        _viewModel.CompleteShellLoad();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsShellLoading))
        {
            ToggleLoadingOverlay(_viewModel.IsShellLoading);
        }
    }

    private void ToggleLoadingOverlay(bool show)
    {
        if (LoadingOverlay is null)
        {
            return;
        }

        LoadingOverlay.BeginAnimation(OpacityProperty, null);

        var duration = TimeSpan.FromMilliseconds(260);
        var animation = new DoubleAnimation
        {
            To = show ? 1d : 0d,
            Duration = new Duration(duration),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
        };

        if (show)
        {
            LoadingOverlay.Opacity = 0d;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingOverlay.IsHitTestVisible = true;
            animation.From = 0d;
            LoadingOverlay.BeginAnimation(OpacityProperty, animation);
        }
        else
        {
            animation.Completed += (_, _) =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingOverlay.IsHitTestVisible = false;
            };

            LoadingOverlay.BeginAnimation(OpacityProperty, animation);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (_frameNavigationService is not null)
        {
            _frameNavigationService.LoadCompleted -= OnInitialNavigationCompleted;
            _frameNavigationService = null;
        }
    }
}