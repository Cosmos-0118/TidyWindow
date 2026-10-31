using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace TidyWindow.Next.Desktop;

internal enum Appearance { System, Light, Dark }

internal sealed class AppearanceSettings(string path)
{
    public static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "appearance.json");

    public Appearance Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<Appearance>(File.ReadAllText(path));
            return Enum.IsDefined(value) ? value : Appearance.System;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Appearance.System; }
    }

    public void Save(Appearance appearance)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(appearance));
        File.Move(temporary, path, true);
    }

    /// <summary>The theme a window should wear: the one picked here, or the one Windows is using when none was picked.</summary>
    public static ElementTheme ThemeFor(Appearance appearance, UISettings systemColors)
    {
        var background = systemColors.GetColorValue(UIColorType.Background);
        var systemLight = background.R * 0.299 + background.G * 0.587 + background.B * 0.114 > 128;
        return appearance == Appearance.Light || appearance == Appearance.System && systemLight ? ElementTheme.Light : ElementTheme.Dark;
    }
}
