using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using TidyWindow.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

public partial class InstallHubCatalogView : UserControl
{
    private const double CardWidth = 320d;
    private const double CardHeight = 230d;
    private const double CardSpacing = 18d;
    private const double VerticalAllowance = 320d;
    private const int MinPageSize = 6;
    private const int MaxPageSize = 24;
    private const int PageSizeUpdateDebounceMs = 75;
    private const double WidthBucketSize = 24d;
    private const double HeightBucketSize = 24d;

    private readonly DispatcherTimer _pageSizeUpdateTimer;
    private double _hostWidth;
    private double _hostHeight;
    private InstallHubViewModel? _viewModel;
    private ScrollViewer? _parentScrollViewer;
    private int _lastWidthBucket = -1;
    private int _lastHeightBucket = -1;

    public InstallHubCatalogView()
    {
        InitializeComponent();
        _pageSizeUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PageSizeUpdateDebounceMs)
        };
        _pageSizeUpdateTimer.Tick += OnPageSizeUpdateTimerTick;

        Loaded += (_, _) => SchedulePageSizeUpdate();
        Unloaded += OnViewUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnViewDataContextChanged;
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        _pageSizeUpdateTimer.Stop();
        DetachViewModel();
    }

    private void OnPageSizeUpdateTimerTick(object? sender, EventArgs e)
    {
        _pageSizeUpdateTimer.Stop();
        UpdatePageSize();
    }

    private void SchedulePageSizeUpdate()
    {
        _pageSizeUpdateTimer.Stop();
        _pageSizeUpdateTimer.Start();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // Clear cached scroll viewer reference when becoming visible
            // to force re-discovery in case the visual tree has changed
            _parentScrollViewer = null;
            _lastWidthBucket = -1;
            _lastHeightBucket = -1;
            SchedulePageSizeUpdate();
        }
    }

    private void OnViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        _viewModel = e.NewValue as InstallHubViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        SchedulePageSizeUpdate();
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        _parentScrollViewer = null;
        _lastWidthBucket = -1;
        _lastHeightBucket = -1;
    }

    private void OnCatalogHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _hostWidth = e.NewSize.Width;
        _hostHeight = e.NewSize.Height;
        SchedulePageSizeUpdate();
    }

    private void OnCatalogItemsLoaded(object sender, RoutedEventArgs e)
    {
        SchedulePageSizeUpdate();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(InstallHubViewModel.CatalogCurrentPage), StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ScrollViewer? scrollViewer = EnsureParentScrollViewer();
            if (scrollViewer is null || CatalogListCard is null)
            {
                return;
            }

            try
            {
                var transform = CatalogListCard.TransformToAncestor(scrollViewer);
                var relativePoint = transform.Transform(new System.Windows.Point(0, 0));
                var targetOffset = scrollViewer.VerticalOffset + relativePoint.Y;
                scrollViewer.ScrollToVerticalOffset(Math.Max(0, targetOffset - 12));
            }
            catch (InvalidOperationException)
            {
                CatalogListCard.BringIntoView();
            }
        }));
    }

    private ScrollViewer? EnsureParentScrollViewer()
    {
        if (_parentScrollViewer is not null)
        {
            return _parentScrollViewer;
        }

        _parentScrollViewer = FindParentScrollViewer(this);
        return _parentScrollViewer;
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void UpdatePageSize()
    {
        var width = _hostWidth > 0 ? _hostWidth : ActualWidth;
        var viewportHeight = EnsureParentScrollViewer()?.ViewportHeight ?? 0d;
        var height = viewportHeight > 0
            ? viewportHeight
            : (_hostHeight > 0 ? _hostHeight : ActualHeight);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var widthBucket = (int)Math.Floor(width / WidthBucketSize);
        var heightBucket = (int)Math.Floor(height / HeightBucketSize);
        if (widthBucket == _lastWidthBucket && heightBucket == _lastHeightBucket)
        {
            return;
        }

        _lastWidthBucket = widthBucket;
        _lastHeightBucket = heightBucket;

        var availableWidth = Math.Max(CardWidth, width);
        var availableHeight = Math.Max(CardHeight * 2, height - VerticalAllowance);

        var columns = Math.Max(1, (int)Math.Floor((availableWidth + CardSpacing) / (CardWidth + CardSpacing)));
        var rows = Math.Max(2, (int)Math.Floor((availableHeight / (CardHeight + CardSpacing))));
        var pageSize = Math.Clamp(columns * rows, MinPageSize, MaxPageSize);

        if (DataContext is InstallHubViewModel viewModel && viewModel.CatalogPageSize != pageSize)
        {
            viewModel.CatalogPageSize = pageSize;
        }
    }
}
