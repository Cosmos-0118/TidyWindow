using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace TidyWindow.App.Controls;

public partial class SimpleProgressBar : System.Windows.Controls.UserControl
{
    private static readonly Brush DefaultFillBrush = new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops = new GradientStopCollection
        {
            new(Color.FromRgb(0x63, 0x8B, 0xFF), 0),
            new(Color.FromRgb(0x7C, 0x3A, 0xED), 1)
        }
    };

    private static readonly Brush DefaultTrackBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x13, 0x25));
    private static readonly Brush DefaultTextBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));

    private static readonly DependencyPropertyKey PercentageTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PercentageText), typeof(string), typeof(SimpleProgressBar), new PropertyMetadata("0%"));

    public static readonly DependencyProperty PercentageTextProperty = PercentageTextPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SimpleProgressBar), new PropertyMetadata(0d, OnProgressChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SimpleProgressBar), new PropertyMetadata(1d, OnProgressChanged));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(SimpleProgressBar), new PropertyMetadata("Processing"));

    public static readonly DependencyProperty ShowPercentageProperty = DependencyProperty.Register(
        nameof(ShowPercentage), typeof(bool), typeof(SimpleProgressBar), new PropertyMetadata(true));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SimpleProgressBar), new PropertyMetadata(DefaultFillBrush));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SimpleProgressBar), new PropertyMetadata(DefaultTrackBrush));

    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground), typeof(Brush), typeof(SimpleProgressBar), new PropertyMetadata(DefaultTextBrush));

    public SimpleProgressBar()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateProgress();
        SizeChanged += (_, _) => UpdateProgress();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public bool ShowPercentage
    {
        get => (bool)GetValue(ShowPercentageProperty);
        set => SetValue(ShowPercentageProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush TextForeground
    {
        get => (Brush)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    public string PercentageText
    {
        get => (string)GetValue(PercentageTextProperty);
        private set => SetValue(PercentageTextPropertyKey, value);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SimpleProgressBar control)
        {
            control.UpdateProgress();
        }
    }

    private void UpdateProgress()
    {
        var max = Maximum;
        if (max <= 0)
        {
            max = 1;
        }

        var fraction = Math.Clamp(Value / max, 0d, 1d);
        if (FillTransform is not null)
        {
            FillTransform.ScaleX = fraction;
        }

        PercentageText = $"{fraction * 100:0}%";
    }
}
