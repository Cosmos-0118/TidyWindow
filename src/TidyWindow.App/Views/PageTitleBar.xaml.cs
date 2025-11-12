using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Controls;
using Brush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaPoint = System.Windows.Point;
using WpfUserControl = System.Windows.Controls.UserControl;
using ControlsOrientation = System.Windows.Controls.Orientation;

namespace TidyWindow.App.Views;

[ContentProperty(nameof(BodyContent))]
public partial class PageTitleBar : WpfUserControl
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
        new PropertyMetadata(new CornerRadius(24)));

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
        LoadComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private const double MediumLayoutBreakpoint = 960d;
    private const double CompactLayoutBreakpoint = 720d;
    private string _currentLayoutState = string.Empty;
    private ColumnDefinition? _trailingColumn;
    private ContentPresenter? _trailingPresenter;
    private StackPanel? _textHost;
    private StackPanel? _titleRowPanel;
    private Border? _badgeHost;

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CaptureResponsiveTargets();
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;
        UpdateResponsiveState(ActualWidth);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateResponsiveState(e.NewSize.Width);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
    }

    private void UpdateResponsiveState(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            return;
        }

        var targetState = width <= CompactLayoutBreakpoint
            ? "Compact"
            : width <= MediumLayoutBreakpoint
                ? "Medium"
                : "Wide";

        if (string.Equals(_currentLayoutState, targetState, StringComparison.Ordinal))
        {
            return;
        }

        ApplyLayoutState(targetState);
        _currentLayoutState = targetState;
    }

    private void ApplyLayoutState(string state)
    {
        switch (state)
        {
            case "Compact":
                ApplyCompactLayout();
                break;
            case "Medium":
                ApplyMediumLayout();
                break;
            default:
                ApplyWideLayout();
                break;
        }
    }

    private void ApplyWideLayout()
    {
        if (!EnsureResponsiveTargets())
        {
            return;
        }

        var trailingColumn = _trailingColumn;
        var trailingPresenter = _trailingPresenter;
        var textHost = _textHost;
        var titleRowPanel = _titleRowPanel;
        var badgeHost = _badgeHost;

        if (trailingColumn is null || trailingPresenter is null || textHost is null || titleRowPanel is null || badgeHost is null)
        {
            return;
        }

        trailingColumn.Width = GridLength.Auto;
        Grid.SetRow(trailingPresenter, 0);
        Grid.SetColumn(trailingPresenter, 2);
        Grid.SetColumnSpan(trailingPresenter, 1);
        trailingPresenter.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        trailingPresenter.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        trailingPresenter.Margin = new Thickness(20, 0, 0, 0);
        titleRowPanel.Orientation = ControlsOrientation.Horizontal;
        textHost.ClearValue(FrameworkElement.MarginProperty);
        badgeHost.ClearValue(FrameworkElement.MarginProperty);
    }

    private void ApplyMediumLayout()
    {
        if (!EnsureResponsiveTargets())
        {
            return;
        }

        var trailingColumn = _trailingColumn;
        var trailingPresenter = _trailingPresenter;
        var textHost = _textHost;
        var titleRowPanel = _titleRowPanel;
        var badgeHost = _badgeHost;

        if (trailingColumn is null || trailingPresenter is null || textHost is null || titleRowPanel is null || badgeHost is null)
        {
            return;
        }

        trailingColumn.Width = new GridLength(0d, GridUnitType.Pixel);
        Grid.SetRow(trailingPresenter, 1);
        Grid.SetColumn(trailingPresenter, 0);
        Grid.SetColumnSpan(trailingPresenter, 3);
        trailingPresenter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        trailingPresenter.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        trailingPresenter.Margin = new Thickness(0, 18, 0, 0);
        titleRowPanel.Orientation = ControlsOrientation.Horizontal;
        textHost.Margin = new Thickness(0);
        badgeHost.Margin = new Thickness(12, 0, 0, 0);
    }

    private void ApplyCompactLayout()
    {
        if (!EnsureResponsiveTargets())
        {
            return;
        }

        var trailingColumn = _trailingColumn;
        var trailingPresenter = _trailingPresenter;
        var textHost = _textHost;
        var titleRowPanel = _titleRowPanel;
        var badgeHost = _badgeHost;

        if (trailingColumn is null || trailingPresenter is null || textHost is null || titleRowPanel is null || badgeHost is null)
        {
            return;
        }

        trailingColumn.Width = new GridLength(0d, GridUnitType.Pixel);
        Grid.SetRow(trailingPresenter, 1);
        Grid.SetColumn(trailingPresenter, 0);
        Grid.SetColumnSpan(trailingPresenter, 3);
        trailingPresenter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        trailingPresenter.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        trailingPresenter.Margin = new Thickness(0, 16, 0, 0);
        titleRowPanel.Orientation = ControlsOrientation.Vertical;
        textHost.Margin = new Thickness(0, 12, 0, 0);
        badgeHost.Margin = new Thickness(0, 8, 0, 0);
    }

    private bool EnsureResponsiveTargets()
    {
        CaptureResponsiveTargets();
        return _trailingColumn is not null
               && _trailingPresenter is not null
               && _textHost is not null
               && _titleRowPanel is not null
               && _badgeHost is not null;
    }

    private void CaptureResponsiveTargets()
    {
        _trailingColumn ??= FindName("TrailingColumn") as ColumnDefinition;
        _trailingPresenter ??= FindName("TrailingPresenter") as ContentPresenter;
        _textHost ??= FindName("TextHost") as StackPanel;
        _titleRowPanel ??= FindName("TitleRowPanel") as StackPanel;
        _badgeHost ??= FindName("BadgeHost") as Border;
    }

    private void LoadComponent()
    {
        var assemblyName = GetType().Assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            assemblyName = "TidyWindow";
        }

        var resourceLocator = new Uri($"/{assemblyName};component/Views/PageTitleBar.xaml", UriKind.Relative);
        System.Windows.Application.LoadComponent(this, resourceLocator);
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
