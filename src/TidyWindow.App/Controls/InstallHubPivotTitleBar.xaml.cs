using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

using WpfUserControl = System.Windows.Controls.UserControl;
using MediaPoint = System.Windows.Point;
using MediaColor = System.Windows.Media.Color;

namespace TidyWindow.App.Controls;

public partial class InstallHubPivotTitleBar : WpfUserControl
{
    private TranslateTransform? _sweepTransform;
    private bool _isSweepInitialized;

    public InstallHubPivotTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartSweep();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _sweepTransform?.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void StartSweep()
    {
        if (SweepRect is null)
        {
            return;
        }

        if (!_isSweepInitialized)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new MediaPoint(0, 0),
                EndPoint = new MediaPoint(1, 0),
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };

            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x55, 0x38, 0xBD, 0xF8), 0.42));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0xAA, 0x43, 0xB9, 0xF5), 0.5));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x55, 0x38, 0xBD, 0xF8), 0.58));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));

            _sweepTransform = new TranslateTransform { X = -1, Y = 0 };
            brush.RelativeTransform = _sweepTransform;
            SweepRect.Fill = brush;
            _isSweepInitialized = true;
        }

        if (_sweepTransform is null)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = -1.0,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(2.2),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        _sweepTransform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
