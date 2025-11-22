using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

public partial class InstallHubBundlesView : UserControl
{
    private const double CompactActionsBreakpoint = 880d;

    private bool _defaultsCaptured;
    private Thickness _selectedActionsDefaultMargin;
    private Orientation _selectedActionsDefaultOrientation;
    private HorizontalAlignment _selectedActionsDefaultHorizontalAlignment;
    private Thickness _secondaryActionButtonDefaultMargin;
    private readonly Thickness _stackedActionsMargin = new(0, 16, 0, 0);
    private double _maxDetailsHeight;

    public InstallHubBundlesView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_defaultsCaptured)
        {
            _selectedActionsDefaultMargin = SelectedBundleActionsPanel.Margin;
            _selectedActionsDefaultOrientation = SelectedBundleActionsPanel.Orientation;
            _selectedActionsDefaultHorizontalAlignment = SelectedBundleActionsPanel.HorizontalAlignment;
            _secondaryActionButtonDefaultMargin = SelectedBundleSecondaryButton.Margin;
            _defaultsCaptured = true;
        }

        ApplyActionLayout(ActualWidth);
        Dispatcher.InvokeAsync(() => SelectedBundleDetailsCard.MinHeight = SelectedBundleDetailsGrid.ActualHeight, DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ApplyActionLayout(e.NewSize.Width);
        }
    }

    private void ApplyActionLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            return;
        }

        var compact = width < CompactActionsBreakpoint;
        if (compact)
        {
            SelectedBundleActionsPanel.Orientation = Orientation.Vertical;
            SelectedBundleActionsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            SelectedBundleActionsPanel.Margin = _stackedActionsMargin;
            SelectedBundleSecondaryButton.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            SelectedBundleActionsPanel.Orientation = _selectedActionsDefaultOrientation;
            SelectedBundleActionsPanel.HorizontalAlignment = _selectedActionsDefaultHorizontalAlignment;
            SelectedBundleActionsPanel.Margin = _selectedActionsDefaultMargin;
            SelectedBundleSecondaryButton.Margin = _secondaryActionButtonDefaultMargin;
        }
    }

    private void SelectedBundleDetailsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0)
        {
            return;
        }

        if (e.NewSize.Height > _maxDetailsHeight)
        {
            _maxDetailsHeight = e.NewSize.Height;
        }

        SelectedBundleDetailsCard.MinHeight = Math.Max(SelectedBundleDetailsCard.MinHeight, _maxDetailsHeight);
    }

}
