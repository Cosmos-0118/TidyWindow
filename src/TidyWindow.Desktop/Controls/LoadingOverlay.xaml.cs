using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>Blurs the area it covers and shows a short message with an animation, without moving anything underneath.
/// Loads shorter than <see cref="ShowDelay"/> never show it, and once shown it stays for <see cref="MinimumVisible"/>, so it never flashes.</summary>
public sealed partial class LoadingOverlay : UserControl
{
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(260);

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LoadingOverlay), new PropertyMetadata(false, (sender, _) => ((LoadingOverlay)sender).ActiveChanged()));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("Loading…", (sender, _) => ((LoadingOverlay)sender).TextChanged()));
    public static readonly DependencyProperty CancelTextProperty = DependencyProperty.Register(nameof(CancelText), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("", (sender, _) => ((LoadingOverlay)sender).UpdateCancel()));
    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(nameof(Heading), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("", (sender, _) => ((LoadingOverlay)sender).DetailChanged()));
    public static readonly DependencyProperty CounterProperty = DependencyProperty.Register(nameof(Counter), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("", (sender, _) => ((LoadingOverlay)sender).DetailChanged()));
    public static readonly DependencyProperty StepsProperty = DependencyProperty.Register(nameof(Steps), typeof(object), typeof(LoadingOverlay), new PropertyMetadata(null, (sender, _) => ((LoadingOverlay)sender).DetailChanged()));
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    /// <summary>What is being worked on, in bold above the message. Empty hides it.</summary>
    public string Heading { get => (string)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    /// <summary>A small "1 of 3" above the heading. Empty hides it.</summary>
    public string Counter { get => (string)GetValue(CounterProperty); set => SetValue(CounterProperty, value); }
    /// <summary>What has already finished, shown as a checklist under the message. Null or empty hides it.</summary>
    public object? Steps { get => GetValue(StepsProperty); set => SetValue(StepsProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Label of an optional button that raises <see cref="Cancelled"/>. Empty hides it.</summary>
    public string CancelText { get => (string)GetValue(CancelTextProperty); set => SetValue(CancelTextProperty, value); }
    public event RoutedEventHandler? Cancelled;

    private enum Pending { None, Show, Hide, Collapse }
    private readonly DispatcherQueueTimer timer;
    private readonly bool animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private Pending pending;
    private bool visible;
    private long shownAt;

    public LoadingOverlay()
    {
        InitializeComponent();
        Message.Text = LiveText.Text = Text;
        var settings = new Windows.UI.ViewManagement.UISettings();
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            // High contrast: no blur or gradients, just the user's own window and text colors.
            var foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
            Layer.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)) { Opacity = 0.92 };
            LeftPane.Fill = GoldPane.Fill = QuietPane.Fill = foreground;
            Spinner.Foreground = foreground;
        }
        Layer.SizeChanged += (_, _) => Resize();
        timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Tick();
        ActualThemeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => OutputPalette.Changed += Rebuild;
        Unloaded += (_, _) => { timer.Stop(); StopAnimation(); OutputPalette.Changed -= Rebuild; };
    }

    private const int MaxLogLines = 300;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private object? watching;
    private void StepsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Rebuild();

    // A job that reports steps gets the console; a plain spinner keeps its one centred line.
    private bool ConsoleMode => Steps is not null;

    private void DetailChanged()
    {
        HeadingText.Text = Heading; HeadingText.Visibility = Heading.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CounterText.Text = Counter; CounterText.Visibility = Counter.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(watching, Steps))
        {
            if (watching is System.Collections.Specialized.INotifyCollectionChanged old) old.CollectionChanged -= StepsChanged;
            watching = Steps;
            if (watching is System.Collections.Specialized.INotifyCollectionChanged next) next.CollectionChanged += StepsChanged;
        }
        LogScroll.Visibility = ConsoleMode ? Visibility.Visible : Visibility.Collapsed;
        Message.Visibility = ConsoleMode ? Visibility.Collapsed : Visibility.Visible;
        Rebuild();
        Resize();
    }

    private void TextChanged()
    {
        Message.Text = LiveText.Text = Text;
        if (ConsoleMode) ScrollToEnd(follow: true);
    }

    private void Rebuild()
    {
        if (!ConsoleMode) return;
        // Stay glued to the newest line, unless the user scrolled up to read something.
        var follow = LogScroll.ScrollableHeight - LogScroll.VerticalOffset < 24;
        var lines = (Steps as System.Collections.IEnumerable)?.Cast<object?>().Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? [];
        var light = ActualTheme == ElementTheme.Light;
        LogLines.Children.Clear();
        var shown = lines.Skip(Math.Max(0, lines.Count - MaxLogLines)).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var line = shown[i];
            var tone = CommandOutputFormatter.ClassifyStep(line);
            var (glyph, glyphTone) = tone switch
            {
                OutputTone.Error => ("✗", OutputTone.Error),
                OutputTone.Warning => ("!", OutputTone.Warning),
                _ => ("✓", OutputTone.Success)
            };
            var problem = tone is OutputTone.Error or OutputTone.Warning;
            var text = new TextBlock { FontFamily = Mono, FontSize = 12, LineHeight = 18, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, OpacityTransition = new ScalarTransition() };
            text.Inlines.Add(new Run { Text = glyph + "  ", Foreground = OutputPalette.Brush(glyphTone, light), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Inlines.Add(new Run { Text = line, Foreground = OutputPalette.Brush(problem ? tone : OutputTone.Normal, light) });
            // The newest few stay sharp and older ones recede, so the eye stays on what just happened. Problems never fade far.
            var age = shown.Count - 1 - i;
            text.Tag = Math.Max(problem ? 0.7 : 0.35, 1 - 0.14 * Math.Max(0, age - 2));
            LogLines.Children.Add(text);
        }
        ApplyFade();
        ScrollToEnd(follow);
    }

    private bool revealed;
    private void ApplyFade() { foreach (var child in LogLines.Children) child.Opacity = revealed ? 1 : (double)((FrameworkElement)child).Tag; }
    // Pointing at the log brings every line back, so older output can be read and copied.
    private void LogPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { revealed = true; ApplyFade(); }
    private void LogPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { revealed = false; ApplyFade(); }

    private void ScrollToEnd(bool follow)
    {
        if (!follow) return;
        DispatcherQueue.TryEnqueue(() => { LogScroll.UpdateLayout(); LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true); });
    }

    // The overlay is dropped onto whatever region a page gives it (a whole page, one row, a card), so the console works from the
    // room it actually has: it takes a share of the width, and the height left once everything else showing has been counted.
    // In a short or narrow region the animation steps aside and the padding tightens so the log stays readable.
    private void Resize()
    {
        if (!ConsoleMode)
        {
            Art.Visibility = Visibility.Visible; Panel.Spacing = 18; Panel.Padding = new Thickness(24);
            Panel.MaxWidth = Heading.Length > 0 ? 420 : 320;
            return;
        }
        var width = Layer.ActualWidth; var height = Layer.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var compact = height < 400 || width < 460;
        var pad = compact ? 14.0 : 24.0; var gap = compact ? 10.0 : 18.0;
        Art.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Panel.Spacing = gap; Panel.Padding = new Thickness(pad);
        var consoleWidth = Math.Min(Math.Clamp(width * 0.5, 360, 720), Math.Max(200, width - pad * 2));
        LogScroll.Width = consoleWidth; Panel.MaxWidth = consoleWidth + pad * 2; LiveText.MaxWidth = Math.Max(120, consoleWidth - 56);
        var used = pad * 2;
        if (!compact) used += 72 + gap;
        if (HeadingText.Visibility == Visibility.Visible || CounterText.Visibility == Visibility.Visible)
            used += gap + (HeadingText.Visibility == Visibility.Visible ? 22 : 0) + (CounterText.Visibility == Visibility.Visible ? 16 : 0);
        if (CancelButton.Visibility == Visibility.Visible) used += gap + 32;
        // A steady height, so the animation above never moves as lines arrive: about a quarter of the room, within what is left.
        LogScroll.Height = Math.Clamp(Math.Min(height - used, height * 0.28), 72, 300);
    }

    private void UpdateCancel()
    {
        CancelButton.Content = CancelText;
        CancelButton.Visibility = CancelText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Resize();
    }
    private void CancelClicked(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, e);

    private void ActiveChanged()
    {
        timer.Stop(); pending = Pending.None;
        if (IsActive)
        {
            if (visible) { Layer.Opacity = 1; Layer.IsHitTestVisible = true; return; }
            Schedule(Pending.Show, ShowDelay);
        }
        else if (visible)
        {
            var remaining = MinimumVisible - TimeSpan.FromMilliseconds(Environment.TickCount64 - shownAt);
            if (remaining > TimeSpan.Zero) Schedule(Pending.Hide, remaining); else Hide();
        }
    }

    private void Schedule(Pending next, TimeSpan after) { pending = next; timer.Interval = after; timer.Start(); }

    private void Tick()
    {
        var action = pending; pending = Pending.None;
        switch (action)
        {
            case Pending.Show: Show(); break;
            case Pending.Hide: Hide(); break;
            case Pending.Collapse:
                Layer.Visibility = Visibility.Collapsed; StopAnimation(); visible = false; break;
        }
    }

    private void Show()
    {
        visible = true; shownAt = Environment.TickCount64;
        Layer.Visibility = Visibility.Visible; Layer.IsHitTestVisible = true;
        if (animate) (snap ??= BuildSnap()).Begin();
        Spinner.IsActive = animate;
        // Raise opacity on the next tick so the fade-in animates from the collapsed state.
        DispatcherQueue.TryEnqueue(() => { if (visible && pending != Pending.Collapse) Layer.Opacity = 1; });
    }

    private void Hide()
    {
        Layer.Opacity = 0; Layer.IsHitTestVisible = false;
        Schedule(Pending.Collapse, FadeOut);
    }

    private void StopAnimation() { snap?.Stop(); Spinner.IsActive = false; }

    private Storyboard? snap;
    // Each pane drifts out of place and springs back, one after another: scattered, snapped, held, scattered again.
    private Storyboard BuildSnap()
    {
        var board = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        (UIElement pane, CompositeTransform move, double x, double y, double angle, double delay)[] panes =
        [
            (LeftPane, LeftMove, -8, 4, -10, 0), (GoldPane, GoldMove, 10, -9, 12, 0.1), (QuietPane, QuietMove, 7, 10, -8, 0.2)
        ];
        foreach (var (pane, move, x, y, angle, delay) in panes)
        {
            Animate(board, move, "TranslateX", x, 0, delay);
            Animate(board, move, "TranslateY", y, 0, delay);
            Animate(board, move, "Rotation", angle, 0, delay);
            Animate(board, pane, "Opacity", 0.3, 1, delay);
        }
        return board;
    }

    private static void Animate(Storyboard board, DependencyObject target, string property, double scattered, double settled, double delay)
    {
        var animation = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(delay) };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = scattered });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.6), Value = settled, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(1.25), Value = settled });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(1.85), Value = scattered, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        board.Children.Add(animation);
    }
}
