using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// What Windows says about animations, high contrast and its colours, read in one guarded place. Some unpackaged installations refuse
/// these settings objects (AccessibilitySettings fails with 0x80070490), and they are read while the window and its controls are being
/// built, where a throw becomes a XAML load failure that ends the app before anything is seen. Here a refusal just means the plain
/// default: animations on, no high contrast, the colour the caller would have used anyway.
/// </summary>
internal static class SystemUi
{
    private static readonly Lazy<UISettings?> settings = new(() => Read<UISettings?>(() => new UISettings(), null));
    private static bool? highContrast;

    /// <summary>The shared settings object, or null when Windows would not provide one.</summary>
    public static UISettings? Settings => settings.Value;

    public static bool AnimationsEnabled => Read(() => Settings?.AnimationsEnabled ?? true, true);

    /// <summary>Whether a high contrast theme is on. Read once, and again after <see cref="Refresh"/>, which runs when the system colours change.</summary>
    public static bool HighContrast => highContrast ??= Read(() => new AccessibilitySettings().HighContrast, false);

    /// <summary>A system colour, or <paramref name="fallback"/> when it cannot be read.</summary>
    public static Color Color(UIColorType type, Color fallback) => Read(() => Settings is { } s ? s.GetColorValue(type) : fallback, fallback);

    public static void Refresh() => highContrast = null;

    /// <summary>Follows changes to the system colours (theme, accent, high contrast). False when Windows would not report them, in which case the app keeps the colours it started with.</summary>
    public static bool Watch(TypedEventHandler<UISettings, object> handler) => Read(() => { if (Settings is not { } s) return false; s.ColorValuesChanged += handler; return true; }, false);

    public static void Unwatch(TypedEventHandler<UISettings, object> handler) => Read(() => { if (Settings is { } s) s.ColorValuesChanged -= handler; return true; }, false);

    // Looks are never worth a crash: whatever the reason a setting cannot be read, the default stands.
    private static T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }
}
