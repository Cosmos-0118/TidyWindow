using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class PathPilotPage : Page
{
    private readonly PathPilotViewModel _viewModel;
    private readonly Thickness _scrollViewerCompactMargin = new(24);
    private readonly Thickness _scrollViewerStackedMargin = new(16, 24, 16, 24);
    private readonly Thickness _defaultScrollViewerMargin;

    private const double CompactBreakpoint = 1180d;
    private const double StackedBreakpoint = 980d;

    public PathPilotPage(PathPilotViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _defaultScrollViewerMargin = ContentScrollViewer.Margin;

        Loaded += OnLoaded;
        ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetCachedInteractionState();
        UpdateResponsiveLayout(ContentScrollViewer.ActualWidth);

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

    private void ContentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        }
    }

    private void UpdateResponsiveLayout(double viewportWidth)
    {
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0d)
        {
            return;
        }

        if (viewportWidth < StackedBreakpoint)
        {
            ContentScrollViewer.Margin = _scrollViewerStackedMargin;
            return;
        }

        if (viewportWidth < CompactBreakpoint)
        {
            ContentScrollViewer.Margin = _scrollViewerCompactMargin;
            return;
        }

        ContentScrollViewer.Margin = _defaultScrollViewerMargin;
    }

}
