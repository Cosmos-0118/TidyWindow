using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class PathPilotPage : Page
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
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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
        _viewModel.CancelInFlightWork();
    }

    private void OnPageChanged(object? sender, EventArgs e)
    {
        _runtimesScrollViewer ??= FindScrollViewer(RuntimesList);
        _runtimesScrollViewer?.ScrollToVerticalOffset(0);
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

}
