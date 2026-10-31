using TidyWindow.Next.Desktop;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class AppearanceSettingsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AppearanceChoiceSurvivesReload(int choice)
    {
        var value = (Appearance)choice;
        var file = Path.Combine(Path.GetTempPath(), "tidywindow-theme-" + Guid.NewGuid().ToString("N"), "appearance.json");
        var settings = new AppearanceSettings(file);
        Assert.Equal(Appearance.System, settings.Load());
        settings.Save(value);
        Assert.Equal(value, new AppearanceSettings(file).Load());
        File.WriteAllText(file, "999");
        Assert.Equal(Appearance.System, settings.Load());
        File.WriteAllText(file, "invalid json");
        Assert.Equal(Appearance.System, settings.Load());
    }
}
