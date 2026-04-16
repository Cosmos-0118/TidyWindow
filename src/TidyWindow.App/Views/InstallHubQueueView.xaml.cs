using System;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

public partial class InstallHubQueueView : UserControl
{
    private const double CompactMarginBreakpoint = 860d;
    private const double CompactHeightBreakpoint = 760d;
    private const double MediumHeightBreakpoint = 920d;

    private const double QueueCardCompactMinHeight = 430d;
    private const double QueueCardMediumMinHeight = 520d;
    private const double QueueCardExpandedMinHeight = 620d;

    private const double QueueListCompactMinHeight = 230d;
    private const double QueueListMediumMinHeight = 300d;
    private const double QueueListExpandedMinHeight = 420d;

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

        double queueCardMinHeight;
        double queueListMinHeight;

        if (viewportHeight <= CompactHeightBreakpoint)
        {
            queueCardMinHeight = QueueCardCompactMinHeight;
            queueListMinHeight = QueueListCompactMinHeight;
        }
        else if (viewportHeight <= MediumHeightBreakpoint)
        {
            queueCardMinHeight = QueueCardMediumMinHeight;
            queueListMinHeight = QueueListMediumMinHeight;
        }
        else
        {
            queueCardMinHeight = QueueCardExpandedMinHeight;
            queueListMinHeight = QueueListExpandedMinHeight;
        }

        if (Math.Abs(QueueTimelineCard.MinHeight - queueCardMinHeight) > 0.1)
        {
            QueueTimelineCard.MinHeight = queueCardMinHeight;
        }

        if (Math.Abs(QueueTimelineListHost.MinHeight - queueListMinHeight) > 0.1)
        {
            QueueTimelineListHost.MinHeight = queueListMinHeight;
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
