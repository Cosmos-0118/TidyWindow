namespace TidyWindow.Next.Desktop;

internal sealed record AdaptiveLayout(double SidebarWidth, double Padding, bool CompactManagers, bool CompactResults)
{
    /// <summary>The sidebar rests as a narrow icon rail and opens over the page, so the page never has to make room for it.</summary>
    public const double RailWidth = 56;
    public const double OpenWidth = 184;

    public static AdaptiveLayout ForWidth(double width)
    {
        var padding = width < 700 ? 16 : width < 1100 ? 24 : 36;
        var content = Math.Max(0, width - RailWidth - 2 * padding);
        return new(RailWidth, padding, content < 600, content < 720);
    }
}
