using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>Where PATH lives. The user's own list can be written directly; the Windows-wide list only by something running as administrator.</summary>
public interface IPathStore
{
    /// <summary>The value exactly as stored, with %VARIABLES% unexpanded.</summary>
    string? Read(PathScope scope);
    string? Variable(PathScope scope, string name);
    /// <param name="previous">The value the caller based this edit on. A store that cannot trust the caller's view refuses to write if the stored value has since changed.</param>
    void Write(PathScope scope, string value, string? previous);
}

[SupportedOSPlatform("windows")]
public sealed class RegistryPathStore : IPathStore
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public string? Read(PathScope scope) => Variable(scope, "Path");

    public string? Variable(PathScope scope, string name)
    {
        using var key = scope == PathScope.User ? Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment") : Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MachineKey);
        return key?.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(PathScope scope, string value, string? previous)
    {
        if (scope == PathScope.Machine) throw new UnauthorizedAccessException("Changing the Windows-wide PATH needs administrator permission.");
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true) ?? throw new InvalidOperationException("Windows has no user environment to change.");
        // Written as an expandable string: plain text would turn %USERPROFILE% and friends into literal words and break every program that relies on them.
        key.SetValue("Path", value, Microsoft.Win32.RegistryValueKind.ExpandString);
    }

    /// <summary>Only the elevated helper calls this: the same write as above, on the Windows-wide list.</summary>
    public void WriteMachine(string value)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MachineKey, writable: true) ?? throw new InvalidOperationException("Windows has no system environment to change.");
        key.SetValue("Path", value, Microsoft.Win32.RegistryValueKind.ExpandString);
    }
}

public static class EnvironmentBroadcast
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    /// <summary>Tells running programs (Explorer, new terminals) that the environment changed, so they pick it up without a restart.</summary>
    public static void Notify()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 3000, out _); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}

public sealed record PathBackupItem(string Id, DateTime Created, string Raw, string Why);

/// <summary>A copy of the user's PATH from before every change, so any change can be taken back.</summary>
public sealed class PathBackup(string folder)
{
    private const int Keep = 30;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public PathBackupItem Record(string raw, string why)
    {
        Directory.CreateDirectory(folder);
        var created = DateTime.UtcNow;
        var item = new PathBackupItem(created.ToString("yyyyMMdd-HHmmss-fff"), created, raw, why);
        File.WriteAllText(Path.Combine(folder, item.Id + ".json"), JsonSerializer.Serialize(item, Json));
        foreach (var old in Directory.EnumerateFiles(folder, "*.json").OrderByDescending(x => x).Skip(Keep))
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return item;
    }

    public PathBackupItem? Latest()
    {
        if (!Directory.Exists(folder)) return null;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderByDescending(x => x))
        {
            try { if (JsonSerializer.Deserialize<PathBackupItem>(File.ReadAllText(file), Json) is { } item) return item; }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return null;
    }

    public void Discard(PathBackupItem item)
    {
        try { File.Delete(Path.Combine(folder, item.Id + ".json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed record PathChange(bool Changed, string Message);

/// <summary>
/// The edits Path Doctor offers, all on one PATH list: the user's own, or the Windows-wide one when given a store that can write it.
/// Every change saves the old value first, checks that the list still looks like the one the person was shown, and tells running programs afterwards.
/// </summary>
public sealed class PathDoctor(IPathStore store, PathBackup backup, PathAnalyzer analyzer, Action broadcast, PathScope target = PathScope.User)
{
    public const string Stale = "Your PATH changed while this page was open. It has been checked again, so look it over and try once more.";
    private string Where => target == PathScope.User ? "your PATH" : "the Windows-wide PATH";
    private IReadOnlyList<PathEntry> Own(PathSnapshot snapshot) => target == PathScope.User ? snapshot.User : snapshot.Machine;

    public PathSnapshot Load() => analyzer.Analyze(store.Read(PathScope.Machine), store.Read(target));
    public PathBackupItem? LastBackup => backup.Latest();
    public string? Variable(string name) => store.Variable(PathScope.User, name) ?? store.Variable(PathScope.Machine, name);

    public PathChange Remove(PathEntry entry) => Edit($"Removed {entry.Raw}", entry, items => { items.RemoveAt(entry.Index); return $"Removed {entry.Raw} from {Where}."; });

    public PathChange Move(PathEntry entry, int to) => Edit($"Moved {entry.Raw}", entry, items =>
    {
        to = Math.Clamp(to, 0, items.Count - 1);
        if (to == entry.Index) return null;
        items.RemoveAt(entry.Index); items.Insert(to, entry.Raw);
        return to == 0 ? $"{entry.Raw} is now searched first." : $"Moved {entry.Raw} to position {to + 1}.";
    });

    public PathChange Add(string folder)
    {
        folder = folder.Trim().Trim('"');
        if (folder.Length == 0 || !Directory.Exists(folder)) return new(false, "That folder does not exist.");
        var key = PathAnalyzer.Key(folder);
        var snapshot = Load();
        if (snapshot.Search.Any(x => PathAnalyzer.Key(x.Folder) == key)) return new(false, $"That folder is already in {Where}.");
        var raw = store.Read(target);
        Save($"Added {folder}", PathEdit.Add(raw, folder.Length > 3 ? folder.TrimEnd('\\') : folder), raw);
        return new(true, $"Added {folder} to {Where}.");
    }

    /// <summary>Adds several folders in one change, so a single Undo takes all of them back.</summary>
    public PathChange AddMany(IEnumerable<string> folders)
    {
        var snapshot = Load();
        var known = snapshot.Search.Select(x => PathAnalyzer.Key(x.Folder)).ToHashSet();
        var add = new List<string>();
        foreach (var folder in folders.Select(x => x.Trim().Trim('"')))
            if (folder.Length > 0 && Directory.Exists(folder) && known.Add(PathAnalyzer.Key(folder))) add.Add(folder.Length > 3 ? folder.TrimEnd('\\') : folder);
        if (add.Count == 0) return new(false, $"Those folders are already in {Where}.");
        var raw = store.Read(target);
        var items = PathEdit.Items(raw);
        items.AddRange(add);
        Save(add.Count == 1 ? $"Added {add[0]}" : $"Added {add.Count} folders", PathEdit.Join(items), raw);
        return new(true, add.Count == 1 ? $"Added {add[0]} to {Where}." : $"Added {add.Count} folders to {Where}.");
    }

    /// <summary>Removes the user's entries that point nowhere or repeat an earlier one. Everything else, including their order, stays as it is.</summary>
    public PathChange FixProblems()
    {
        var snapshot = Load();
        var drop = Own(snapshot).Where(x => x.NeedsFixing).ToList();
        if (drop.Count == 0) return new(false, "Nothing needed fixing.");
        var raw = store.Read(target);
        Save("Removed duplicate and missing folders", PathEdit.Remove(raw, drop.Select(x => x.Index)), raw);
        var duplicates = drop.Count(x => x.Duplicate);
        var missing = drop.Count(x => x.Missing && !x.Duplicate);
        var parts = new List<string>();
        if (duplicates > 0) parts.Add(duplicates == 1 ? "1 repeated folder" : $"{duplicates} repeated folders");
        if (missing > 0) parts.Add(missing == 1 ? "1 folder that no longer exists" : $"{missing} folders that no longer exist");
        return new(true, "Removed " + string.Join(" and ", parts) + ".");
    }

    public PathChange Undo()
    {
        if (backup.Latest() is not { } item) return new(false, "There is nothing to undo.");
        store.Write(target, item.Raw, store.Read(target));
        backup.Discard(item);
        broadcast();
        return new(true, $"Restored {Where} from before: {item.Why.ToLowerInvariant()}.");
    }

    private PathChange Edit(string why, PathEntry entry, Func<List<string>, string?> change)
    {
        var raw = store.Read(target);
        var items = PathEdit.Items(raw);
        if (entry.Index >= items.Count || items[entry.Index] != entry.Raw) return new(false, Stale);
        var message = change(items);
        if (message is null) return new(false, "Nothing to change.");
        Save(why, PathEdit.Join(items), raw);
        return new(true, message);
    }

    private void Save(string why, string value, string? previous)
    {
        backup.Record(previous ?? "", why);
        store.Write(target, value, previous);
        broadcast();
    }
}
