using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>One registry value kept exactly as it was, so a removed key can be put back.</summary>
public sealed record ValueBackup(string Name, string Kind, string? Text = null, long? Number = null, byte[]? Bytes = null, string[]? Lines = null);

/// <summary>A registry key with all its values and subkeys. This is what an undo restores.</summary>
public sealed record KeyBackup(string Path, IReadOnlyList<ValueBackup> Values, IReadOnlyList<KeyBackup> Children);

/// <summary>
/// Removes and restores the few registry keys that leftovers of a failed install live in. It only ever touches keys that match an allow-list,
/// whoever asks, and it hands back a full copy of what it removed so the change can be undone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryRepair(IReadOnlyList<Regex> allowed) : IKeyEditor
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow64Uninstall = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Installer = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer";

    private static Regex Rule(string pattern) => new("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Windows-wide keys. Only the elevated helper uses these.</summary>
    public static RegistryRepair Machine { get; } = new(
    [
        Rule(@"HKLM\\" + Regex.Escape(Uninstall) + @"\\[^\\]+"),
        Rule(@"HKLM\\" + Regex.Escape(Wow64Uninstall) + @"\\[^\\]+"),
        Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\UserData\\S-1-[0-9-]+\\Products\\[0-9A-F]{32}"),
        Rule(@"HKLM\\SOFTWARE\\Classes\\Installer\\(?:Products|Features)\\[0-9A-F]{32}"),
        Rule(@"HKLM\\" + Regex.Escape(Installer) + @"\\InProgress")
    ]);

    /// <summary>Keys of the signed-in user, which need no administrator permission.</summary>
    public static RegistryRepair User { get; } = new(
    [
        Rule(@"HKCU\\" + Regex.Escape(Uninstall) + @"\\[^\\]+"),
        Rule(@"HKCU\\" + Regex.Escape(Wow64Uninstall) + @"\\[^\\]+"),
        Rule(@"HKCU\\SOFTWARE\\Microsoft\\Installer\\(?:Products|Features)\\[0-9A-F]{32}")
    ]);

    public bool IsAllowed(string path) => !path.Contains("..") && allowed.Any(x => x.IsMatch(path));

    /// <summary>Copies the key and removes it. A key that is already gone counts as done, with no copy.</summary>
    public KeyBackup? Delete(string path)
    {
        if (!IsAllowed(path)) throw new InvalidOperationException("TidyWindow does not change that part of the registry.");
        var (root, sub) = Open(path);
        using var parent = root;
        using (var key = parent.OpenSubKey(sub))
        {
            if (key is null) return null;
            var copy = Capture(key, path);
            key.Close();
            parent.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            return copy;
        }
    }

    /// <summary>Writes a removed key back. Values that already exist are replaced.</summary>
    public void Restore(KeyBackup backup)
    {
        if (!IsAllowed(backup.Path)) throw new InvalidOperationException("TidyWindow does not change that part of the registry.");
        var (root, sub) = Open(backup.Path);
        using var parent = root;
        Write(parent, sub, backup);
    }

    public bool Exists(string path)
    {
        var (root, sub) = Open(path);
        using var parent = root;
        using var key = parent.OpenSubKey(sub);
        return key is not null;
    }

    private static (RegistryKey Root, string Sub) Open(string path)
    {
        var split = path.IndexOf('\\');
        if (split < 0) throw new ArgumentException("A registry path needs a key under the hive.", nameof(path));
        var hive = path[..split].ToUpperInvariant() switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            _ => throw new ArgumentException("Only HKLM and HKCU are supported.", nameof(path))
        };
        // The Windows-wide WOW6432Node keys are named in full, so one view reaches every key without the redirection getting in the way.
        return (RegistryKey.OpenBaseKey(hive, RegistryView.Registry64), path[(split + 1)..]);
    }

    private static KeyBackup Capture(RegistryKey key, string path)
    {
        var values = new List<ValueBackup>();
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values.Add(kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => new(name, kind.ToString(), Text: data as string ?? ""),
                RegistryValueKind.DWord => new(name, kind.ToString(), Number: Convert.ToInt64(data) & 0xFFFFFFFFL),
                RegistryValueKind.QWord => new(name, kind.ToString(), Number: Convert.ToInt64(data)),
                RegistryValueKind.MultiString => new(name, kind.ToString(), Lines: data as string[] ?? []),
                _ => new(name, RegistryValueKind.Binary.ToString(), Bytes: data as byte[] ?? [])
            });
        }
        var children = new List<KeyBackup>();
        foreach (var child in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(child);
            if (sub is not null) children.Add(Capture(sub, path + "\\" + child));
        }
        return new(path, values, children);
    }

    private static void Write(RegistryKey parent, string sub, KeyBackup backup)
    {
        using var key = parent.CreateSubKey(sub, writable: true);
        foreach (var value in backup.Values)
        {
            var kind = Enum.TryParse<RegistryValueKind>(value.Kind, out var parsed) ? parsed : RegistryValueKind.String;
            switch (kind)
            {
                case RegistryValueKind.String or RegistryValueKind.ExpandString: key.SetValue(value.Name, value.Text ?? "", kind); break;
                case RegistryValueKind.DWord: key.SetValue(value.Name, unchecked((int)(value.Number ?? 0)), kind); break;
                case RegistryValueKind.QWord: key.SetValue(value.Name, value.Number ?? 0, kind); break;
                case RegistryValueKind.MultiString: key.SetValue(value.Name, value.Lines ?? [], kind); break;
                default: key.SetValue(value.Name, value.Bytes ?? [], RegistryValueKind.Binary); break;
            }
        }
        foreach (var child in backup.Children)
            Write(parent, sub + "\\" + child.Path[(child.Path.LastIndexOf('\\') + 1)..], child);
    }
}

/// <summary>Everything one repair changed, kept so the whole repair can be undone in one go.</summary>
public sealed record RepairBatch(string Id, DateTimeOffset Created, string Title, IReadOnlyList<KeyBackup> Keys);

/// <summary>Remembers the last few repairs on disk, as plain JSON files.</summary>
public sealed class RepairHistory(string folder, int keep = 10)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public RepairBatch Save(string title, IReadOnlyList<KeyBackup> keys, DateTimeOffset? now = null)
    {
        Directory.CreateDirectory(folder);
        var created = now ?? DateTimeOffset.Now;
        var batch = new RepairBatch(created.ToString("yyyyMMdd-HHmmss-fff"), created, title, keys);
        File.WriteAllText(FileFor(batch.Id), JsonSerializer.Serialize(batch, Json));
        foreach (var old in All().Skip(keep)) Remove(old.Id);
        return batch;
    }

    public IReadOnlyList<RepairBatch> All()
    {
        if (!Directory.Exists(folder)) return [];
        var batches = new List<RepairBatch>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<RepairBatch>(File.ReadAllText(file), Json) is { } batch) batches.Add(batch); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return batches.OrderByDescending(x => x.Created).ToList();
    }

    public RepairBatch? Latest() => All().FirstOrDefault();

    public void Remove(string id)
    {
        try { File.Delete(FileFor(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string FileFor(string id) => Path.Combine(folder, id + ".json");
}
