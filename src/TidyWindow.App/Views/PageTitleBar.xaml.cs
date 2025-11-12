using System.Windows;
using System.Windows.Media;
using System.Windows.Markup;
using Brush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaPoint = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

[ContentProperty(nameof(BodyContent))]
public partial class PageTitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PageTitleBar),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(PageTitleBar),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph),
        typeof(string),
        typeof(PageTitleBar),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BadgeTextProperty = DependencyProperty.Register(
        nameof(BadgeText),
        typeof(string),
        typeof(PageTitleBar),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(PageTitleBar),
        new PropertyMetadata(CreateDefaultAccentBrush()));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(CornerRadius),
        typeof(PageTitleBar),
        new PropertyMetadata(new CornerRadius(18)));

    public static readonly DependencyProperty TrailingContentProperty = DependencyProperty.Register(
        nameof(TrailingContent),
        typeof(object),
        typeof(PageTitleBar),
        new PropertyMetadata(null));

    public static readonly DependencyProperty BodyContentProperty = DependencyProperty.Register(
        nameof(BodyContent),
        typeof(object),
        typeof(PageTitleBar),
        new PropertyMetadata(null));

    static PageTitleBar()
    {
        BackgroundProperty.OverrideMetadata(
            typeof(PageTitleBar),
            new FrameworkPropertyMetadata(CreateDefaultBackgroundBrush()));

        PaddingProperty.OverrideMetadata(
            typeof(PageTitleBar),
            new FrameworkPropertyMetadata(new Thickness(22, 14, 22, 16)));
    }

    public PageTitleBar()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public string BadgeText
    {
        get => (string)GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }

    public object? BodyContent
    {
        get => GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }

    private static Brush CreateDefaultAccentBrush()
    {
        var brush = new SolidColorBrush(MediaColor.FromRgb(0x38, 0xBD, 0xF8));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateDefaultBackgroundBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new MediaPoint(0, 0),
            EndPoint = new MediaPoint(1, 1)
        };

        brush.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0x0B, 0x15, 0x25), 0));
        brush.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0x11, 0x1F, 0x33), 0.5));
        brush.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0x08, 0x12, 0x22), 1));

        brush.Freeze();
        return brush;
    }
}
