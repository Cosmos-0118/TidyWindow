using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TidyWindow.Next.Desktop;

/// <summary>Where the rounds of one check fell, as positions from 0 (left) to 1 (right) on a shared scale, with the middle result.</summary>
public sealed record RangeBand(double Low, double High, double Mid);

/// <summary>Two thin tracks that show, side by side, where the rounds of two checks fell. Either one can be missing.</summary>
public sealed partial class RangeBar : UserControl
{
    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(nameof(Before), typeof(RangeBand), typeof(RangeBar), new PropertyMetadata(null, (sender, _) => ((RangeBar)sender).Refresh()));
    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(nameof(After), typeof(RangeBand), typeof(RangeBar), new PropertyMetadata(null, (sender, _) => ((RangeBar)sender).Refresh()));

    public RangeBand? Before { get => (RangeBand?)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public RangeBand? After { get => (RangeBand?)GetValue(AfterProperty); set => SetValue(AfterProperty, value); }

    public RangeBar()
    {
        InitializeComponent();
        Refresh();
    }

    private void TrackResized(object sender, SizeChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (BeforeTrack is null) return;
        Place(Before, BeforeTrack, BeforeBand, BeforeMid, BeforeLabel);
        Place(After, AfterTrack, AfterBand, AfterMid, AfterLabel);
    }

    private static void Place(RangeBand? band, Grid track, Border fill, Border mid, TextBlock label)
    {
        var shown = band is not null;
        var visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        track.Visibility = visibility; label.Visibility = visibility;
        var width = track.ActualWidth;
        if (band is null || width <= 0) return;
        var low = Math.Clamp(band.Low, 0, 1) * width; var high = Math.Clamp(band.High, 0, 1) * width;
        // A very narrow band is still drawn wide enough to see.
        var size = Math.Max(high - low, 6);
        var left = Math.Clamp(low, 0, Math.Max(width - size, 0));
        fill.Width = size; fill.Margin = new Thickness(left, 0, 0, 0);
        mid.Margin = new Thickness(Math.Clamp(band.Mid * width - 1, 0, Math.Max(width - 2, 0)), 0, 0, 0);
    }
}
