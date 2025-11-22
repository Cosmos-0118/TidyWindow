using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

public partial class InstallHubQueueView : UserControl
{
    private const double TwoColumnBreakpoint = 980d;
    private const double CompactMarginBreakpoint = 720d;
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

        ApplyLayout(QueueScrollViewer.ActualWidth);

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

        QueueDrawerToggle.IsChecked = false;
    }

    private void QueueScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ApplyLayout(e.NewSize.Width);
        }
    }

    private void ApplyLayout(double viewportWidth)
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

        var stackLayout = viewportWidth <= TwoColumnBreakpoint;
        if (stackLayout)
        {
            QueuePrimaryColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            QueueSecondaryColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
            QueueSecondaryColumnDefinition.MinWidth = 0;
            QueueSpacerColumnDefinition.Width = new GridLength(0);

            QueueSecondaryColumnHost.Visibility = Visibility.Collapsed;
            QueueDrawerToggle.Visibility = Visibility.Visible;
        }
        else
        {
            QueuePrimaryColumnDefinition.Width = new GridLength(3, GridUnitType.Star);
            QueueSecondaryColumnDefinition.Width = new GridLength(2, GridUnitType.Star);
            QueueSecondaryColumnDefinition.MinWidth = 320;
            QueueSpacerColumnDefinition.Width = new GridLength(24);

            QueueSecondaryColumnHost.Visibility = Visibility.Visible;
            QueueDrawerToggle.Visibility = Visibility.Collapsed;
            if (QueueDrawerToggle.IsChecked == true)
            {
                QueueDrawerToggle.IsChecked = false;
            }
        }
    }

    private void DetailsDrawerOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        QueueDrawerToggle.IsChecked = false;
    }

    private void DetailsDrawerPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void CloseDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        QueueDrawerToggle.IsChecked = false;
    }

    private static bool ThicknessEquals(Thickness left, Thickness right)
    {
        return Math.Abs(left.Left - right.Left) < 0.1
            && Math.Abs(left.Top - right.Top) < 0.1
            && Math.Abs(left.Right - right.Right) < 0.1
            && Math.Abs(left.Bottom - right.Bottom) < 0.1;
    }
}
