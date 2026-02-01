using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class PathPilotPage : Page, INavigationAware
{
    private readonly PathPilotViewModel _viewModel;
    private ScrollViewer? _runtimesScrollViewer;

    public PathPilotPage(PathPilotViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Activate();
        _viewModel.PageChanged -= OnPageChanged;
        _viewModel.PageChanged += OnPageChanged;
        _viewModel.ResetCachedInteractionState();

        if (_runtimesScrollViewer is null)
        {
            _runtimesScrollViewer = FindScrollViewer(RuntimesList);
        }

        if (_viewModel.Runtimes.Count > 0)
        {
            return;
        }

        if (_viewModel.RefreshCommand is IAsyncRelayCommand asyncCommand)
        {
            if (!asyncCommand.IsRunning && asyncCommand.CanExecute(null))
            {
                await asyncCommand.ExecuteAsync(null);
            }
        }
        else if (_viewModel.RefreshCommand is ICommand command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PageChanged -= OnPageChanged;
        _viewModel.Deactivate();
        _runtimesScrollViewer = null;
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        _runtimesScrollViewer ??= FindScrollViewer(RuntimesList);
        _runtimesScrollViewer?.ScrollToVerticalOffset(0);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.NewValue is bool isVisible && isVisible)
        {
            _viewModel.Activate();
        }
        else
        {
            _viewModel.Deactivate();
        }
    }

    private void OnRuntimesLoaded(object sender, RoutedEventArgs e)
    {
        _runtimesScrollViewer ??= FindScrollViewer(RuntimesList);
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
        // Clear cached scroll viewer to force re-discovery
        _runtimesScrollViewer = null;

        // Re-subscribe to page changed events
        _viewModel.PageChanged -= OnPageChanged;
        _viewModel.PageChanged += OnPageChanged;

        _viewModel.Activate();
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        _runtimesScrollViewer = null;
        _viewModel.Deactivate();
    }
}
