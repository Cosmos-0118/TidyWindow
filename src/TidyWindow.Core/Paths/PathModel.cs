namespace TidyWindow.Next.Core;

public enum PathScope { Machine, User }

/// <summary>One folder in a PATH list, as it was written and as Windows reads it.</summary>
public sealed record PathEntry(PathScope Scope, int Index, string Raw, string Folder, bool Exists, string? DuplicateOf, bool StoreApps)
{
    public bool Missing => !Exists;
    public bool Duplicate => DuplicateOf is not null;
    public bool NeedsFixing => Missing || Duplicate;
}

/// <summary>Both PATH lists in the order Windows searches them: the Windows-wide list first, then the user's own.</summary>
public sealed record PathSnapshot(IReadOnlyList<PathEntry> Machine, IReadOnlyList<PathEntry> User, string MachineRaw, string UserRaw)
{
    public IEnumerable<PathEntry> Search => Machine.Concat(User);
}

public sealed class PathAnalyzer(Func<string, string> expand, Func<string, bool> directoryExists)
{
    public const string MachineLabel = "the Windows-wide list";
    public const string UserLabel = "your list";

    /// <summary>Two spellings of the same folder compare equal: case, slashes and a trailing backslash do not matter.</summary>
    public static string Key(string folder)
    {
        var value = folder.Trim().Trim('"').Replace('/', '\\');
        while (value.Length > 3 && value.EndsWith('\\')) value = value[..^1];
        return value.ToLowerInvariant();
    }

    public PathSnapshot Analyze(string? machineRaw, string? userRaw)
    {
        var seen = new Dictionary<string, string>();
        return new(Parse(PathScope.Machine, machineRaw, seen), Parse(PathScope.User, userRaw, seen), machineRaw ?? "", userRaw ?? "");
    }

    private List<PathEntry> Parse(PathScope scope, string? raw, Dictionary<string, string> seen)
    {
        var entries = new List<PathEntry>();
        var items = PathEdit.Items(raw);
        for (var i = 0; i < items.Count; i++)
        {
            var folder = expand(items[i].Trim('"'));
            // A folder that still contains %NAME% after expanding, or lives on a network share, cannot be judged cheaply or safely, so it counts as present.
            var unknown = folder.Contains('%') || folder.StartsWith(@"\\", StringComparison.Ordinal);
            var exists = unknown || directoryExists(folder);
            var key = Key(folder);
            string? duplicateOf = null;
            if (seen.TryGetValue(key, out var first)) duplicateOf = first;
            else seen[key] = scope == PathScope.Machine ? MachineLabel : UserLabel;
            entries.Add(new PathEntry(scope, i, items[i], folder, exists, duplicateOf, key.Contains(@"\microsoft\windowsapps", StringComparison.Ordinal)));
        }
        return entries;
    }
}

/// <summary>Pure edits on the text of a PATH value. They keep each entry as the person (or an installer) wrote it, including %VARIABLES%.</summary>
public static class PathEdit
{
    public static List<string> Items(string? raw) => (raw ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    public static string Join(IEnumerable<string> items) => string.Join(';', items);

    public static string Remove(string? raw, IEnumerable<int> indexes)
    {
        var drop = indexes.ToHashSet();
        return Join(Items(raw).Where((_, i) => !drop.Contains(i)));
    }

    public static string Move(string? raw, int index, int to)
    {
        var items = Items(raw);
        if (index < 0 || index >= items.Count) return Join(items);
        to = Math.Clamp(to, 0, items.Count - 1);
        var item = items[index];
        items.RemoveAt(index); items.Insert(to, item);
        return Join(items);
    }

    public static string Add(string? raw, string folder, bool first = false)
    {
        var items = Items(raw);
        if (first) items.Insert(0, folder); else items.Add(folder);
        return Join(items);
    }
}
