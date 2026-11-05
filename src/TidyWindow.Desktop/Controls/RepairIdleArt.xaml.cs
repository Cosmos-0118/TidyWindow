using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace TidyWindow.Next.Desktop;

/// <summary>Repair's resting picture: a wrench that fixes an installer, a folder and a shortcut in turn. It plays only while it is visible,
/// and holds a still frame when the system has animations turned off.</summary>
public sealed partial class RepairIdleArt : UserControl
{
    private const double Loop = 6.0;
    private const double Stride = 80; // distance between two cards
    private readonly bool animate = SystemUi.AnimationsEnabled;
    private Storyboard? board;
    private bool running;

    public RepairIdleArt()
    {
        InitializeComponent();
        Hot1.Opacity = 1; // The still frame: the first card is already mended.
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
    }

    private void Sync() { if (Visibility == Visibility.Visible && IsLoaded) Start(); else Stop(); }

    private void Start()
    {
        if (!animate || running) return;
        (board ??= Build()).Begin();
        running = true;
    }

    private void Stop()
    {
        if (!running) return;
        board?.Stop();
        running = false;
        Hot1.Opacity = 1;
    }

    private readonly record struct Key(double Time, double Value, EasingFunctionBase? Ease = null);

    private static readonly EasingFunctionBase Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 };
    private static readonly EasingFunctionBase Smooth = new CubicEase { EasingMode = EasingMode.EaseInOut };
    private static readonly EasingFunctionBase Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly EasingFunctionBase In = new CubicEase { EasingMode = EasingMode.EaseIn };

    private Storyboard Build()
    {
        var b = new Storyboard(); // Every track repeats on its own, so the loop point is exact.
        // The wrench glides to each card, hops as it goes and settles with a little spring.
        Track(b, WrenchMove, "TranslateX", new Key(0, 0), new Key(1.3, 0), new Key(1.9, Stride, Spring), new Key(3.3, Stride), new Key(3.9, Stride * 2, Spring), new Key(5.3, Stride * 2), new Key(6.0, 0, Smooth));
        TrackAll(b, WrenchMove, "TranslateY", Hop(1.3, 1.9, 8), Hop(3.3, 3.9, 8), Hop(5.3, 6.0, 12));
        // On each card it turns back and forth, like tightening a bolt; between cards it tilts a little as it travels.
        TrackAll(b, WrenchMove, "Rotation", [new Key(0, 0)], Twist(0.0), Hop(1.3, 1.9, -10), Twist(1.9), Hop(3.3, 3.9, -10), Twist(3.9), Hop(5.3, 6.0, -12));
        // Each card is mended while the wrench is on it: it lights up with a tick and pops. The last hands over to the first at the loop point.
        Lit(b, Hot1, CardMove1, new Key(0, 1), new Key(1.5, 1), new Key(1.9, 0, Smooth), new Key(5.6, 0), new Key(6.0, 1, Out));
        Lit(b, Hot2, CardMove2, new Key(0, 0), new Key(1.6, 0), new Key(2.0, 1, Spring), new Key(3.5, 1), new Key(3.9, 0, Smooth), new Key(6.0, 0));
        Lit(b, Hot3, CardMove3, new Key(0, 0), new Key(3.7, 0), new Key(4.1, 1, Spring), new Key(5.5, 1), new Key(5.9, 0, Smooth), new Key(6.0, 0));
        // A ring leaves each card as it is mended.
        Ripple(b, Ring1, RingMove1, 0.0); Ripple(b, Ring2, RingMove2, 1.9); Ripple(b, Ring3, RingMove3, 3.9);
        // The cards bob gently, each at its own pace, so the scene never looks frozen between repairs.
        Bob(b, CardMove1, 1.7, 0); Bob(b, CardMove2, 2.1, 0.5); Bob(b, CardMove3, 1.9, 1.0);
        return b;
    }

    // Four quick turns, starting a moment after the wrench lands. The times stay clear of the travel keys on either side.
    private static Key[] Twist(double at) => [new Key(at + 0.25, -28, Smooth), new Key(at + 0.5, 12, Smooth), new Key(at + 0.75, -28, Smooth), new Key(at + 1.0, 12, Smooth)];

    private static Key[] Hop(double from, double to, double lift)
    {
        var mid = from + (to - from) * 0.4;
        return [new Key(from, 0), new Key(mid, -lift, Out), new Key(to, 0, In)];
    }

    private static void TrackAll(Storyboard b, DependencyObject target, string property, params Key[][] segments) => Track(b, target, property, segments.SelectMany(x => x).ToArray());

    private static void Track(Storyboard b, DependencyObject target, string property, params Key[] keys)
    {
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        // Several segments are given separately; keys must stay in time order and start at zero.
        var ordered = keys.OrderBy(k => k.Time).ToList();
        if (ordered[0].Time > 0) ordered.Insert(0, new Key(0, ordered[0].Value));
        if (ordered[^1].Time < Loop) ordered.Add(new Key(Loop, ordered[^1].Value));
        foreach (var k in ordered) animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(k.Time), Value = k.Value, EasingFunction = k.Ease });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        b.Children.Add(animation);
    }

    private static void Lit(Storyboard b, UIElement hot, DependencyObject move, params Key[] opacity)
    {
        Track(b, hot, "Opacity", opacity);
        var scale = opacity.Select(k => k with { Value = 1 + 0.12 * k.Value }).ToArray();
        Track(b, move, "ScaleX", scale); Track(b, move, "ScaleY", scale);
    }

    private static void Ripple(Storyboard b, UIElement ring, DependencyObject move, double at)
    {
        // A ring that fires at the very start has no quiet lead-in, or two keys would share one moment.
        var lead = at > 0;
        Key[] fade = [.. (lead ? new[] { new Key(0, 0) } : []), new Key(at, 0.6), new Key(at + 0.8, 0, Out), new Key(Loop, 0)];
        Track(b, ring, "Opacity", fade);
        Key[] grow = [.. (lead ? new[] { new Key(0, 0.85) } : []), new Key(at, 0.85), new Key(at + 0.8, 1.7, Out), new Key(Loop, 1.7)];
        Track(b, move, "ScaleX", grow); Track(b, move, "ScaleY", grow);
    }

    private static void Bob(Storyboard b, DependencyObject move, double seconds, double delay)
    {
        var animation = new DoubleAnimation { From = 0, To = -4, Duration = TimeSpan.FromSeconds(seconds), BeginTime = TimeSpan.FromSeconds(delay), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(animation, move);
        Storyboard.SetTargetProperty(animation, "TranslateY");
        b.Children.Add(animation);
    }
}
