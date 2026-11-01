namespace TidyWindow.Next.Desktop;

internal sealed record AdaptiveLayout(double SidebarWidth, double Padding, bool CompactManagers, bool CompactResults)
{
    /// <summary>The sidebar rests as a narrow icon rail. On a wide window it opens over the page; on a narrower one the page moves aside, because the open rail would otherwise cover the titles.</summary>
    public const double RailWidth = 56;
    public const double OpenWidth = 184;
    /// <summary>At this width and above there is enough page left that the open sidebar can sit on top of it.</summary>
    public const double OverlayWidth = 1200;

    public static bool PushesContent(double width) => width < OverlayWidth;

    public static AdaptiveLayout ForWidth(double width, bool sidebarOpen = false)
    {
        var sidebar = sidebarOpen && PushesContent(width) ? OpenWidth : RailWidth;
        var padding = width < 700 ? 16 : width < 1100 ? 24 : 36;
        var content = Math.Max(0, width - sidebar - 2 * padding);
        return new(sidebar, padding, content < 600, content < 720);
    }
}
