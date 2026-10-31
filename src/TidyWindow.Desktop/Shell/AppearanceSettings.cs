using System.Text.Json;

namespace TidyWindow.Next.Desktop;

internal enum Appearance { System, Light, Dark }

/// <summary>Which theme the person picked, kept on disk. This file is also compiled into the tests, so it uses nothing from WinUI; the part that does is in AppearanceTheme.cs.</summary>
internal sealed partial class AppearanceSettings(string path)
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
}
