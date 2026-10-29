using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace TidyWindow.Next.Desktop;

/// <summary>Blurs the area it covers and shows a short message with an animation, without moving anything underneath.
/// Loads shorter than <see cref="ShowDelay"/> never show it, and once shown it stays for <see cref="MinimumVisible"/>, so it never flashes.</summary>
public sealed partial class LoadingOverlay : UserControl
{
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(260);

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LoadingOverlay), new PropertyMetadata(false, (sender, _) => ((LoadingOverlay)sender).ActiveChanged()));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("Loading…", (sender, _) => ((LoadingOverlay)sender).Message.Text = ((LoadingOverlay)sender).Text));
    public static readonly DependencyProperty CancelTextProperty = DependencyProperty.Register(nameof(CancelText), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("", (sender, _) => ((LoadingOverlay)sender).UpdateCancel()));
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
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
        Message.Text = Text;
        var settings = new Windows.UI.ViewManagement.UISettings();
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            // High contrast: no blur or gradients, just the user's own window and text colors.
            var foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
            Layer.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)) { Opacity = 0.92 };
            LeftPane.Fill = GoldPane.Fill = QuietPane.Fill = foreground;
        }
        timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Tick();
        Unloaded += (_, _) => { timer.Stop(); StopAnimation(); };
    }

    private void UpdateCancel()
    {
        CancelButton.Content = CancelText;
        CancelButton.Visibility = CancelText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        // Raise opacity on the next tick so the fade-in animates from the collapsed state.
        DispatcherQueue.TryEnqueue(() => { if (visible && pending != Pending.Collapse) Layer.Opacity = 1; });
    }

    private void Hide()
    {
        Layer.Opacity = 0; Layer.IsHitTestVisible = false;
        Schedule(Pending.Collapse, FadeOut);
    }

    private void StopAnimation() => snap?.Stop();

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
