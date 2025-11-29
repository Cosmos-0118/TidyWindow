using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ITrayService _trayService;
    private readonly UserPreferencesService _preferences;
    private readonly PulseGuardService _pulseGuard;
    private readonly IAutomationWorkTracker _workTracker;
    private System.Windows.Navigation.NavigationService? _frameNavigationService;
    private bool _initialNavigationCompleted;
    private bool _autoCloseArmed;

    public MainWindow(MainViewModel viewModel, NavigationService navigationService, ITrayService trayService, UserPreferencesService preferences, PulseGuardService pulseGuard, IAutomationWorkTracker workTracker)
    {
        InitializeComponent();
        _navigationService = navigationService;
        _viewModel = viewModel;
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _pulseGuard = pulseGuard ?? throw new ArgumentNullException(nameof(pulseGuard));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ToggleLoadingOverlay(_viewModel.IsShellLoading);
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        _trayService.Attach(this);
        UpdateMaximizeVisualState();
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

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeVisualState();

        if (WindowState == WindowState.Minimized && _preferences.Current.RunInBackground)
        {
            _trayService.HideToTray(showHint: true);
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
        CancelAutoCloseSubscription();
        base.OnClosed(e);

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (_frameNavigationService is not null)
        {
            _frameNavigationService.LoadCompleted -= OnInitialNavigationCompleted;
            _frameNavigationService = null;
        }

        StateChanged -= OnStateChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_trayService.IsExitRequested && _preferences.Current.RunInBackground)
        {
            e.Cancel = true;
            _trayService.HideToTray(showHint: true);
            return;
        }

        if (!_preferences.Current.RunInBackground)
        {
            var activeWork = _workTracker.GetActiveWork();
            if (activeWork.Count > 0)
            {
                var decision = _pulseGuard.PromptPendingAutomation(activeWork);
                if (decision == PendingAutomationDecision.WaitForCompletion)
                {
                    e.Cancel = true;
                    ArmAutoClose();
                    return;
                }

                CancelAutoCloseSubscription();
            }
        }

        _trayService.PrepareForExit();
        base.OnClosing(e);
    }

    private void ArmAutoClose()
    {
        if (_autoCloseArmed)
        {
            return;
        }

        _autoCloseArmed = true;
        _workTracker.ActiveWorkChanged += OnActiveWorkChanged;
        _viewModel.SetStatusMessage("Waiting for automation to finish before closing...");
    }

    private void CancelAutoCloseSubscription()
    {
        if (!_autoCloseArmed)
        {
            return;
        }

        _workTracker.ActiveWorkChanged -= OnActiveWorkChanged;
        _autoCloseArmed = false;
    }

    private void OnActiveWorkChanged(object? sender, EventArgs e)
    {
        if (!_autoCloseArmed || _workTracker.HasActiveWork)
        {
            return;
        }

        CancelAutoCloseSubscription();

        Dispatcher.Invoke(() =>
        {
            _trayService.PrepareForExit();
            Close();
        });
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            ToggleWindowState();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Swallow drag exceptions that can happen during state transitions.
            }
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeVisualState();
    }

    private void UpdateMaximizeVisualState()
    {
        if (MaximizeGlyph is null)
        {
            return;
        }

        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeGlyph.ToolTip = WindowState == WindowState.Maximized ? "Restore Down" : "Maximize";
    }
}