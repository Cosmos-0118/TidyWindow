using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// One toast. It slides in, slides out when it is dismissed or runs out, waits while the pointer or keyboard is on it, and shows a long message
/// shortened until the person opens it. Movement runs on the compositor, so it stays smooth even while the page behind is busy.
/// </summary>
public sealed partial class ToastCard : UserControl
{
    private static readonly TimeSpan EnterTime = TimeSpan.FromMilliseconds(260);
    private readonly bool animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private ToastItem? item;
    private bool entered, exited, expanded, canExpand;
    private DispatcherQueueTimer? copied;

    public ToastCard()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ToastItem);
        Loaded += (_, _) => Enter();
        Unloaded += (_, _) => { copied?.Stop(); Release(); };
    }

    private void Attach(ToastItem? next)
    {
        if (ReferenceEquals(item, next)) return;
        if (item is not null) { item.PropertyChanged -= ItemChanged; item.Renewed -= Renewed; item.HoldChanged -= SyncBar; Release(); }
        item = next;
        if (item is null) return;
        item.PropertyChanged += ItemChanged; item.Renewed += Renewed; item.HoldChanged += SyncBar;
        // A toast that was told to go before its card existed has nothing to show.
        if (item.Closing) Card.Opacity = 0;
        UpdateActions();
    }

    private void Release()
    {
        // A toast must not stay held by a card that is gone.
        item?.Hold("pointer", false); item?.Hold("focus", false); item?.Hold("open", false);
    }

    private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToastItem.Closing) && item is { Closing: true }) Exit();
        else if (e.PropertyName is nameof(ToastItem.Kind) or nameof(ToastItem.Detail)) UpdateActions();
    }

    // ----- Coming and going -----

    private Visual CardVisual => ElementCompositionPreview.GetElementVisual(Card);

    private void Enter()
    {
        if (entered) return;
        entered = true;
        var visual = CardVisual;
        ElementCompositionPreview.SetIsTranslationEnabled(Card, true);
        if (animate && item is { Closing: false })
        {
            var compositor = visual.Compositor;
            // Quick to start, gentle to land.
            var ease = compositor.CreateCubicBezierEasingFunction(new(0.16f, 1f), new(0.3f, 1f));
            visual.Opacity = 0;
            visual.Properties.InsertVector3("Translation", new(36, 0, 0));
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f, ease); fade.Duration = EnterTime;
            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(1f, Vector3.Zero, ease); slide.Duration = EnterTime;
            visual.StartAnimation("Opacity", fade);
            visual.Properties.StartAnimation("Translation", slide);
        }
        else visual.Opacity = item?.Closing == true ? 0 : 1;
        UpdateActions();
        SyncBar();
    }

    private void Exit()
    {
        if (exited) return;
        exited = true;
        Card.IsHitTestVisible = false;
        BarFill.Visibility = Visibility.Collapsed;
        var visual = CardVisual;
        if (!animate) { visual.Opacity = 0; return; }
        var compositor = visual.Compositor;
        // Starts slowly and speeds away, the opposite of coming in.
        var ease = compositor.CreateCubicBezierEasingFunction(new(0.5f, 0f), new(0.9f, 0.4f));
        var time = ToastCenter.ExitTime - TimeSpan.FromMilliseconds(20);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, ease); fade.Duration = time;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, new(36, 0, 0), ease); slide.Duration = time;
        visual.StartAnimation("Opacity", fade);
        visual.Properties.StartAnimation("Translation", slide);
    }

    /// <summary>The message came up again: a small nudge shows it was refreshed rather than ignored.</summary>
    private void Renewed(bool same)
    {
        SyncBar();
        if (!animate || exited) return;
        var visual = CardVisual;
        visual.CenterPoint = new((float)(Card.ActualWidth / 2), (float)(Card.ActualHeight / 2), 0);
        var nudge = visual.Compositor.CreateVector3KeyFrameAnimation();
        nudge.InsertKeyFrame(0.4f, new(1.02f, 1.02f, 1f)); nudge.InsertKeyFrame(1f, Vector3.One);
        nudge.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Scale", nudge);
    }

    // ----- The timer bar -----

    // The bar shrinks over exactly as long as the toast has left, and stands still while the toast is held.
    private void SyncBar()
    {
        if (item is null || !entered) return;
        if (item.Countdown.Endless || !animate) { BarTrack.Visibility = Visibility.Collapsed; return; }
        BarTrack.Visibility = Visibility.Visible;
        var bar = ElementCompositionPreview.GetElementVisual(BarFill);
        bar.StopAnimation("Scale.X");
        bar.CenterPoint = Vector3.Zero;
        bar.Scale = new((float)item.Countdown.Fraction, 1f, 1f);
        if (item.Countdown.IsHeld || item.Closing) return;
        var shrink = bar.Compositor.CreateScalarKeyFrameAnimation();
        shrink.InsertKeyFrame(1f, 0f, bar.Compositor.CreateLinearEasingFunction());
        shrink.Duration = item.Countdown.Remaining;
        bar.StartAnimation("Scale.X", shrink);
    }

    // ----- Being read -----

    private void PointerOver(object sender, PointerRoutedEventArgs e) => item?.Hold("pointer", true);
    private void PointerAway(object sender, PointerRoutedEventArgs e) => item?.Hold("pointer", false);
    private void FocusInside(object sender, RoutedEventArgs e) => item?.Hold("focus", true);
    private void FocusOutside(object sender, RoutedEventArgs e) => item?.Hold("focus", false);

    // ----- Long messages -----

    private void TrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateActions();

    private void UpdateActions()
    {
        if (item is null) return;
        // The shortened text is hidden while open, so what it says about being cut off is kept from before.
        if (!expanded) canExpand = TitleText.IsTextTrimmed || DetailText.Visibility == Visibility.Visible && DetailText.IsTextTrimmed;
        MoreButton.Visibility = canExpand || expanded ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = item.Detail.Length > 0 && (canExpand || expanded || item.Kind == ToastKind.Warning) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreClick(object sender, RoutedEventArgs e)
    {
        expanded = !expanded;
        ShortTitle.Visibility = DetailText.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        if (item is { Detail.Length: 0 }) DetailText.Visibility = Visibility.Collapsed;
        FullText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Content = expanded ? "Show less" : "Show more";
        // Open for reading: the toast stays until it is closed again or dismissed.
        item?.Hold("open", expanded);
        UpdateActions();
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (item is null) return;
        try
        {
            var package = new DataPackage();
            package.SetText(item.CopyText);
            Clipboard.SetContent(package);
            CopyButton.Content = "Copied";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { CopyButton.Content = "Could not copy"; }
        copied ??= CreateResetTimer();
        copied.Stop(); copied.Start();
    }

    private DispatcherQueueTimer CreateResetTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.6); timer.IsRepeating = false;
        timer.Tick += (_, _) => CopyButton.Content = "Copy";
        return timer;
    }

    private async void ActionClick(object sender, RoutedEventArgs e) { if (item is not null) await item.RunAsync(); }
    private void DismissClick(object sender, RoutedEventArgs e) => item?.Dismiss();
}
