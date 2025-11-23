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
    private readonly Thickness _secondaryStackedMargin = new(0, 24, 0, 0);
    private readonly Thickness _scrollViewerCompactMargin = new(24);
    private readonly Thickness _scrollViewerStackedMargin = new(16, 24, 16, 24);
    private readonly double _primaryColumnDefaultMinWidth;
    private readonly double _secondaryColumnDefaultMinWidth;
    private readonly Thickness _defaultScrollViewerMargin;
    private readonly Thickness _secondaryColumnDefaultMargin;
    private bool _isStackedLayout;

    private const double CompactBreakpoint = 1180d;
    private const double StackedBreakpoint = 980d;

    public PathPilotPage(PathPilotViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _defaultScrollViewerMargin = ContentScrollViewer.Margin;
        _secondaryColumnDefaultMargin = SecondaryColumnHost.Margin;
        _primaryColumnDefaultMinWidth = PrimaryColumnDefinition.MinWidth;
        _secondaryColumnDefaultMinWidth = SecondaryColumnDefinition.MinWidth;

        Loaded += OnLoaded;
        ContentScrollViewer.SizeChanged += ContentScrollViewer_SizeChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
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

        var useStackedLayout = viewportWidth < StackedBreakpoint;
        var useCompactColumns = viewportWidth < CompactBreakpoint;

        if (useStackedLayout)
        {
            if (!_isStackedLayout)
            {
                Grid.SetColumn(SecondaryColumnHost, 0);
                Grid.SetRow(SecondaryColumnHost, 1);
                SecondaryColumnHost.Margin = _secondaryStackedMargin;
                PrimaryColumnDefinition.MinWidth = 0d;
                SecondaryColumnDefinition.MinWidth = 0d;
                PrimaryColumnDefinition.Width = new GridLength(1d, GridUnitType.Star);
                SecondaryColumnDefinition.Width = new GridLength(0d, GridUnitType.Pixel);
                _isStackedLayout = true;
            }

            ContentScrollViewer.Margin = _scrollViewerStackedMargin;
            return;
        }

        if (_isStackedLayout)
        {
            Grid.SetColumn(SecondaryColumnHost, 1);
            Grid.SetRow(SecondaryColumnHost, 0);
            SecondaryColumnHost.Margin = _secondaryColumnDefaultMargin;
            PrimaryColumnDefinition.MinWidth = _primaryColumnDefaultMinWidth;
            SecondaryColumnDefinition.MinWidth = _secondaryColumnDefaultMinWidth;
            _isStackedLayout = false;
        }

        var targetPrimary = useCompactColumns
            ? new GridLength(1d, GridUnitType.Star)
            : new GridLength(3d, GridUnitType.Star);
        var targetSecondary = useCompactColumns
            ? new GridLength(1d, GridUnitType.Star)
            : new GridLength(2d, GridUnitType.Star);

        if (!PrimaryColumnDefinition.Width.Equals(targetPrimary))
        {
            PrimaryColumnDefinition.Width = targetPrimary;
        }

        if (!SecondaryColumnDefinition.Width.Equals(targetSecondary))
        {
            SecondaryColumnDefinition.Width = targetSecondary;
        }

        ContentScrollViewer.Margin = useCompactColumns ? _scrollViewerCompactMargin : _defaultScrollViewerMargin;
    }

}
