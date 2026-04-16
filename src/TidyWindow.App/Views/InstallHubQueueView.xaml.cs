using System;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

public partial class InstallHubQueueView : UserControl
{
    private const double CompactMarginBreakpoint = 860d;
    private const double QueueCardHeightOffset = 250d;
    private const double QueueCardMinHeight = 360d;
    private const double QueueCardMaxHeight = 860d;
    private const double QueueListHeightOffset = 220d;
    private const double QueueListMinHeight = 170d;
    private const double QueueListMaxHeight = 640d;

    private Thickness _defaultMargin = new(32, 0, 32, 24);
    private readonly Thickness _compactMargin = new(20, 0, 20, 24);
    private bool _sizeHandlerAttached;
    private bool _marginCaptured;

    public InstallHubQueueView()
    {
        InitializeComponent();

        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (!_marginCaptured)
        {
            _defaultMargin = QueueScrollViewer.Margin;
            _marginCaptured = true;
        }

        ApplyLayout(QueueScrollViewer.ActualWidth, QueueScrollViewer.ActualHeight);

        if (_sizeHandlerAttached)
        {
            return;
        }

        QueueScrollViewer.SizeChanged += QueueScrollViewer_SizeChanged;
        _sizeHandlerAttached = true;
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_sizeHandlerAttached)
        {
            QueueScrollViewer.SizeChanged -= QueueScrollViewer_SizeChanged;
            _sizeHandlerAttached = false;
        }
    }

    private void QueueScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged || e.HeightChanged)
        {
            ApplyLayout(e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void ApplyLayout(double viewportWidth, double viewportHeight)
    {
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = QueueScrollViewer.ActualWidth;
        }

        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

        var useCompactMargin = viewportWidth <= CompactMarginBreakpoint;
        var targetMargin = useCompactMargin ? _compactMargin : _defaultMargin;
        if (!ThicknessEquals(QueueScrollViewer.Margin, targetMargin))
        {
            QueueScrollViewer.Margin = targetMargin;
        }

        if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = QueueScrollViewer.ActualHeight;
        }

        if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
        {
            return;
        }

        var queueCardHeight = Math.Clamp(viewportHeight - QueueCardHeightOffset, QueueCardMinHeight, QueueCardMaxHeight);
        var queueListHeight = Math.Clamp(queueCardHeight - QueueListHeightOffset, QueueListMinHeight, QueueListMaxHeight);

        if (Math.Abs(QueueTimelineCard.Height - queueCardHeight) > 0.1)
        {
            QueueTimelineCard.Height = queueCardHeight;
        }

        if (Math.Abs(QueueTimelineListHost.Height - queueListHeight) > 0.1)
        {
            QueueTimelineListHost.Height = queueListHeight;
        }

    }

    private static bool ThicknessEquals(Thickness left, Thickness right)
    {
        return Math.Abs(left.Left - right.Left) < 0.1
            && Math.Abs(left.Top - right.Top) < 0.1
            && Math.Abs(left.Right - right.Right) < 0.1
            && Math.Abs(left.Bottom - right.Bottom) < 0.1;
    }
}
