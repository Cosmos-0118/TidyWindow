using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class EssentialsPage : Page, INavigationAware
{
    private const double CompactMarginBreakpoint = 980d;
    private const double QueueCardHeightOffset = 470d;
    private const double QueueCardMinHeight = 300d;
    private const double QueueCardMaxHeight = 640d;
    private const double QueueListHeightOffset = 160d;
    private const double QueueListMinHeight = 140d;
    private const double QueueListMaxHeight = 460d;

    private readonly EssentialsViewModel _viewModel;
    private readonly Controls.EssentialsPivotTitleBar _titleBar;
    private bool _disposed;
    private readonly bool _shouldDisposeOnUnload;
    private MainViewModel? _shellViewModel;
    private System.Windows.Navigation.NavigationService? _navigationService;
    private readonly Thickness _compactScrollMargin = new(20);
    private Thickness _defaultScrollMargin = new(32);
    private bool _scrollMarginCaptured;
    private bool _responsiveLayoutAttached;

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
        EnsureResponsiveLayout();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            AttachTitleBar();
            EnsureResponsiveLayout();
        }
    }

    private void AttachTitleBar()
    {
        // Always refresh the shell view model reference to ensure we have the current one
        _shellViewModel = System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel;
        _shellViewModel?.SetTitleBarContent(_titleBar);

        _navigationService ??= System.Windows.Navigation.NavigationService.GetNavigationService(this);
        if (_navigationService is not null)
        {
            _navigationService.Navigated -= OnNavigated;
            _navigationService.Navigated += OnNavigated;
        }
    }

    private void ClearTitleBarIfOwned()
    {
        if (_shellViewModel is null)
        {
            return;
        }

        if (ReferenceEquals(_shellViewModel.TitleBarContent, _titleBar))
        {
            _shellViewModel.SetTitleBarContent(null);
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
            ClearTitleBarIfOwned();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        DetachResponsiveLayout();

        // Don't detach navigation service for cached pages
        if (_disposed || !_shouldDisposeOnUnload)
        {
            ClearTitleBarIfOwned();
            return;
        }

        // Full cleanup for non-cached pages
        if (_navigationService is not null)
        {
            _navigationService.Navigated -= OnNavigated;
        }

        IsVisibleChanged -= OnIsVisibleChanged;
        Unloaded -= OnPageUnloaded;
        ClearTitleBarIfOwned();
        _viewModel.Dispose();
        _disposed = true;
    }

    private void EnsureResponsiveLayout()
    {
        if (EssentialsScrollViewer is null)
        {
            return;
        }

        if (!_scrollMarginCaptured)
        {
            _defaultScrollMargin = EssentialsScrollViewer.Margin;
            _scrollMarginCaptured = true;
        }

        ApplyResponsiveLayout(EssentialsScrollViewer.ActualWidth, EssentialsScrollViewer.ActualHeight);

        if (_responsiveLayoutAttached)
        {
            return;
        }

        EssentialsScrollViewer.SizeChanged += OnEssentialsScrollViewerSizeChanged;
        _responsiveLayoutAttached = true;
    }

    private void DetachResponsiveLayout()
    {
        if (!_responsiveLayoutAttached || EssentialsScrollViewer is null)
        {
            return;
        }

        EssentialsScrollViewer.SizeChanged -= OnEssentialsScrollViewerSizeChanged;
        _responsiveLayoutAttached = false;
    }

    private void OnEssentialsScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged || e.HeightChanged)
        {
            ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void ApplyResponsiveLayout(double viewportWidth, double viewportHeight)
    {
        if (EssentialsScrollViewer is null)
        {
            return;
        }

        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = EssentialsScrollViewer.ActualWidth;
        }

        if (!double.IsNaN(viewportWidth) && viewportWidth > 0)
        {
            var targetMargin = viewportWidth <= CompactMarginBreakpoint
                ? _compactScrollMargin
                : _defaultScrollMargin;

            if (!ThicknessEquals(EssentialsScrollViewer.Margin, targetMargin))
            {
                EssentialsScrollViewer.Margin = targetMargin;
            }
        }

        if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = EssentialsScrollViewer.ActualHeight;
        }

        if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
        {
            return;
        }

        var queueCardHeight = Math.Clamp(viewportHeight - QueueCardHeightOffset, QueueCardMinHeight, QueueCardMaxHeight);
        var queueRegionHeight = Math.Clamp(queueCardHeight - QueueListHeightOffset, QueueListMinHeight, QueueListMaxHeight);

        if (Math.Abs(EssentialsQueueTimelineCard.Height - queueCardHeight) > 0.1)
        {
            EssentialsQueueTimelineCard.Height = queueCardHeight;
        }

        if (Math.Abs(EssentialsQueueOperationsRegion.Height - queueRegionHeight) > 0.1)
        {
            EssentialsQueueOperationsRegion.Height = queueRegionHeight;
        }
    }

    private static bool ThicknessEquals(Thickness left, Thickness right)
    {
        return Math.Abs(left.Left - right.Left) < 0.1
            && Math.Abs(left.Top - right.Top) < 0.1
            && Math.Abs(left.Right - right.Right) < 0.1
            && Math.Abs(left.Bottom - right.Bottom) < 0.1;
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        AttachTitleBar();
        EnsureResponsiveLayout();
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        ClearTitleBarIfOwned();
        DetachResponsiveLayout();
    }
}
