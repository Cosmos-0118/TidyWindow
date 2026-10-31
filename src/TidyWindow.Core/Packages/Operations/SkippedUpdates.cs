using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>
/// Remembers updates a package manager lists but cannot apply (for example Microsoft Edge, which WinGet sees as
/// upgradable yet refuses to touch because it was installed another way). Each entry holds the version that failed,
/// so the update is offered again once a newer version appears. With no path it lives in memory only.
/// </summary>
public sealed class SkippedUpdates(string? path = null)
{
    private readonly object gate = new();
    private Dictionary<string, string>? entries;

    public bool IsSkipped(ManagedPackage app, string available)
    {
        lock (gate) return Load().TryGetValue(app.Key, out var version) && version.Equals(available, StringComparison.OrdinalIgnoreCase);
    }

    public void Skip(ManagedPackage app, string available)
    {
        lock (gate)
        {
            Load()[app.Key] = available;
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        if (entries is not null) return entries;
        try { entries = path is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { entries = []; }
        return entries;
    }

    private void Save()
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Still skipped for this session. */ }
    }
}
