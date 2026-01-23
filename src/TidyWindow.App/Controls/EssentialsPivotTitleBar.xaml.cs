using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TidyWindow.App.Controls;

public partial class EssentialsPivotTitleBar : System.Windows.Controls.UserControl
{
    public EssentialsPivotTitleBar()
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

        // Build a brush that sweeps across the full bar using a relative transform so it scales with window size.
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x66, 0x8B, 0x5C, 0xF6), 0.45));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0xAA, 0xAA, 0x7C, 0xF6), 0.5));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x66, 0x8B, 0x5C, 0xF6), 0.55));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));

        var transform = new TranslateTransform { X = -1, Y = 0 };
        brush.RelativeTransform = transform;
        SweepRect.Fill = brush;

        var animation = new DoubleAnimation
        {
            From = -1.0,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(2.4),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
