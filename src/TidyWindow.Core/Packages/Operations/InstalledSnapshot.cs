using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Next.Core;

/// <summary>
/// The installed-apps list as it was the last time it was read in full, kept on disk so the Installed page can show something the moment it opens
/// while the package managers (which take seconds, and much longer on a slow PC) are read again. It is only ever a first guess: nothing in it is
/// acted on, and a missing, damaged or out-of-date file is the same as no file.
/// </summary>
public sealed class InstalledSnapshot(string path)
{
    private const int Format = 1;
    private const long MaxBytes = 16_000_000;
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    internal sealed record Stored(int Format, List<Entry> Apps);
    internal sealed record Entry(ManagerKind Manager, string Id, string Name, string Version, string? Available, bool Global, bool Held, string Note, string Source);

    public IReadOnlyList<ManagedPackage> Load()
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxBytes) return [];
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Options);
            if (stored is null || stored.Format != Format || stored.Apps is null) return [];
            return stored.Apps
                .Where(x => Enum.IsDefined(x.Manager) && !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new ManagedPackage(x.Manager, x.Id, x.Name, x.Version ?? "", x.Available, x.Global, x.Held, x.Note ?? "", x.Source ?? ""))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    /// <summary>Replaces the saved list. A failure to save is not worth interrupting anyone for: the next read will try again.</summary>
    public void Save(IEnumerable<ManagedPackage> packages)
    {
        try
        {
            var stored = new Stored(Format, packages.Select(x => new Entry(x.Manager, x.Id, x.Name, x.Version, x.AvailableVersion, x.Global, x.Held, x.Note, x.Source)).ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
}
