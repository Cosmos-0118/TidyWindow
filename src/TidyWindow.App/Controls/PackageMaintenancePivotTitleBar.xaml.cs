using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

using WpfUserControl = System.Windows.Controls.UserControl;
using MediaPoint = System.Windows.Point;
using MediaColor = System.Windows.Media.Color;

namespace TidyWindow.App.Controls;

public partial class PackageMaintenancePivotTitleBar : WpfUserControl
{
    public PackageMaintenancePivotTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartSweep();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        StartSweep();
    }

    private void StartSweep()
    {
        if (SweepRect is null)
        {
            return;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new MediaPoint(0, 0),
            EndPoint = new MediaPoint(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0));
        brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x55, 0x8B, 0x5C, 0xF6), 0.42));
        brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0xAA, 0x8B, 0x5C, 0xF6), 0.5));
        brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x55, 0x8B, 0x5C, 0xF6), 0.58));
        brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));

        var transform = new TranslateTransform { X = -1, Y = 0 };
        brush.RelativeTransform = transform;
        SweepRect.Fill = brush;

        var animation = new DoubleAnimation
        {
            From = -1.0,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(2.3),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
