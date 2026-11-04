using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// One toast. It slides in, slides out when it is dismissed or runs out, waits while the pointer or keyboard is on it, and shows a long message
/// shortened until the person opens it. Movement runs on the compositor, so it stays smooth even while the page behind is busy.
/// </summary>
public sealed partial class ToastCard : UserControl
{
    private static readonly TimeSpan EnterTime = TimeSpan.FromMilliseconds(260);
    private readonly bool animate = SystemUi.AnimationsEnabled;
    private ToastItem? item;
    private bool entered, exited, expanded, canExpand;
    private DispatcherQueueTimer? copied;

    public ToastCard()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ToastItem);
        Loaded += (_, _) => Enter();
        Unloaded += (_, _) => { copied?.Stop(); Release(); };
        ActualThemeChanged += (_, _) => UpdateMark();
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
        UpdateMark();
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
        else if (e.PropertyName == nameof(ToastItem.Kind)) { UpdateMark(); UpdateActions(); }
        else if (e.PropertyName is nameof(ToastItem.Detail) or nameof(ToastItem.ActionLabel)) UpdateActions();
    }

    // ----- The status mark -----

    // One solid glyph per kind: a disc with a tick, a triangle with an exclamation mark, a disc with an "i".
    private void UpdateMark()
    {
        if (item is null) return;
        var (glyph, brush) = item.Kind switch
        {
            ToastKind.Success => ("", "GoodBrush"),
            ToastKind.Warning => ("", "WarnBrush"),
            _ => ("", "InfoBrush"),
        };
        Mark.Glyph = glyph;
        Mark.Foreground = ThemeBrush(brush);
    }

    // A {ThemeResource} cannot be chosen in code, so the brush is read from the theme the card is showing now and read again when it changes.
    private Brush? ThemeBrush(string key)
    {
        var theme = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast" : ActualTheme == ElementTheme.Dark ? "Default" : "Light";
        return Application.Current.Resources.ThemeDictionaries.TryGetValue(theme, out var found) && found is ResourceDictionary colours && colours.TryGetValue(key, out var value)
            ? value as Brush : null;
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
        // Run out but not yet closed by the next tick: there is nothing left to shrink, and the compositor refuses an animation of no length.
        var left = item.Countdown.Remaining;
        if (left < TimeSpan.FromMilliseconds(1)) return;
        var shrink = bar.Compositor.CreateScalarKeyFrameAnimation();
        shrink.InsertKeyFrame(1f, 0f, bar.Compositor.CreateLinearEasingFunction());
        shrink.Duration = left;
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
        // On its own, "Show more" is pulled left by its padding so its text lines up with the message; next to a button it just follows it.
        MoreButton.Margin = item.ActionVisibility == Visibility.Visible ? new Thickness(0) : new Thickness(-8, 0, 0, 0);
        ActionsRow.Visibility = item.ActionVisibility == Visibility.Visible || MoreButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreClick(object sender, RoutedEventArgs e)
    {
        expanded = !expanded;
        // The title stays in its row beside the icon and just stops being cut; the detail swaps to a scrolling, selectable copy.
        TitleText.MaxLines = expanded ? 0 : 2;
        TitleText.TextTrimming = expanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        TitleText.IsTextSelectionEnabled = expanded;
        var hasDetail = item is { Detail.Length: > 0 };
        DetailText.Visibility = !expanded && hasDetail ? Visibility.Visible : Visibility.Collapsed;
        FullText.Visibility = expanded && hasDetail ? Visibility.Visible : Visibility.Collapsed;
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
            ShowCopyResult("", "Copied");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { ShowCopyResult("", "Could not copy"); }
        copied ??= CreateResetTimer();
        copied.Stop(); copied.Start();
    }

    // The icon briefly says what happened (a tick, or a cross), then goes back to the copy icon.
    private void ShowCopyResult(string glyph, string tip)
    {
        CopyIcon.Glyph = glyph;
        ToolTipService.SetToolTip(CopyButton, tip);
    }

    private DispatcherQueueTimer CreateResetTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.6); timer.IsRepeating = false;
        timer.Tick += (_, _) => { CopyIcon.Glyph = ""; ToolTipService.SetToolTip(CopyButton, "Copy this message"); };
        return timer;
    }

    // RunAsync reports its own failures to the center; this is only the last barrier, because an exception leaving a click handler ends the app.
    private async void ActionClick(object sender, RoutedEventArgs e)
    {
        if (item is null) return;
        try { await item.RunAsync(); }
        catch (Exception) { /* already reported; nothing a card could add */ }
    }
    private void DismissClick(object sender, RoutedEventArgs e) => item?.Dismiss();
}
