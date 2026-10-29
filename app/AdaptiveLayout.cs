namespace TidyWindow.Next.Desktop;

internal sealed record AdaptiveLayout(double SidebarWidth, double Padding, bool CompactManagers, bool CompactResults)
{
    public bool NavigationRail => SidebarWidth == 56;
    public static AdaptiveLayout ForWidth(double width)
    {
        var sidebar = width < 860 ? 56 : 184;
        var padding = width < 700 ? 16 : width < 1100 ? 24 : 36;
        var content = Math.Max(0, width - sidebar - 2 * padding);
        return new(sidebar, padding, content < 600, content < 720);
    }
}
