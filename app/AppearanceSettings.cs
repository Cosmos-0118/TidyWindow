using System.Text.Json;

namespace TidyWindow.Next.Desktop;

internal enum Appearance { System, Light, Dark }

internal sealed class AppearanceSettings(string path)
{
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
