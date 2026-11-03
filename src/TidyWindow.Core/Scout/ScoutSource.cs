namespace TidyWindow.Next.Core;

/// <summary>
/// One file or folder as ranking and filtering see it: spans over text that lives somewhere else, so looking at an entry costs no allocation.
/// A <see cref="ScoutEntry"/> and an entry of the folder index (which keeps its entries in a compact form) both become one, so both are judged by the same code.
/// </summary>
internal readonly ref struct ScoutView
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    private readonly ReadOnlySpan<char> path;
    private readonly ScoutScratch? scratch;
    private readonly string? folder;

    /// <summary>A view of an entry that has its full path.</summary>
    public ScoutView(ReadOnlySpan<char> fullPath, ReadOnlySpan<char> name, ReadOnlySpan<char> searchName, bool isFolder, long? size, DateTimeOffset? modified)
    {
        path = fullPath; Name = name; SearchName = searchName; IsFolder = isFolder; Size = size; Modified = modified;
    }

    /// <summary>A view of an entry known by the folder it is in and its name. The full path is only put together if something asks for it, which most entries never need.</summary>
    public ScoutView(string folder, ScoutScratch scratch, ReadOnlySpan<char> name, ReadOnlySpan<char> searchName, bool isFolder, long? size, DateTimeOffset? modified)
    {
        this.folder = folder; this.scratch = scratch; Name = name; SearchName = searchName; IsFolder = isFolder; Size = size; Modified = modified;
    }

    public ReadOnlySpan<char> FullPath => scratch is null ? path : scratch.Full(folder!, Name);
    /// <summary>The last part of the path, as it is written.</summary>
    public ReadOnlySpan<char> Name { get; }
    /// <summary>The name in the form searches compare it in (see <see cref="ScoutQuery.Normalize"/>).</summary>
    public ReadOnlySpan<char> SearchName { get; }
    public bool IsFolder { get; }
    public long? Size { get; }
    public DateTimeOffset? Modified { get; }

    /// <summary>Whether the full path has this text in it, ignoring case. For text without a separator that is whether the folder or the name has it, so no path is needed.</summary>
    public bool PathContains(string text)
    {
        if (scratch is null || text.AsSpan().IndexOfAny(Separators) >= 0) return FullPath.Contains(text, StringComparison.OrdinalIgnoreCase);
        return folder.AsSpan().Contains(text, StringComparison.OrdinalIgnoreCase) || Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    public static ScoutView Of(ScoutEntry entry) =>
        new(entry.FullPath, Path.GetFileName(entry.FullPath.AsSpan().TrimEnd(Separators)), entry.SearchName, entry.IsFolder, entry.Size, entry.Modified);
}

/// <summary>Buffers a source reuses to describe one entry after another. A <see cref="ScoutView"/> is only valid until the next entry is described with the same scratch.</summary>
internal sealed class ScoutScratch
{
    private char[] path = new char[512];
    private char[] name = new char[256];
    private char[] left = new char[512];
    private char[] right = new char[512];

    public ReadOnlySpan<char> Full(string folder, ReadOnlySpan<char> file) => Join(ref path, folder, file);
    public ReadOnlySpan<char> Left(string folder, ReadOnlySpan<char> file) => Join(ref left, folder, file);
    public ReadOnlySpan<char> Right(string folder, ReadOnlySpan<char> file) => Join(ref right, folder, file);

    /// <summary>The name in lowercase, which is its normal form when it is plain ASCII.</summary>
    public ReadOnlySpan<char> Lower(ReadOnlySpan<char> file)
    {
        if (name.Length < file.Length) name = new char[Math.Max(file.Length, name.Length * 2)];
        var length = file.ToLowerInvariant(name);
        return name.AsSpan(0, length);
    }

    /// <summary>Joins the way <see cref="System.IO.Path.Join(string, string)"/> does, without making a string.</summary>
    private static ReadOnlySpan<char> Join(ref char[] buffer, string folder, ReadOnlySpan<char> file)
    {
        var need = folder.Length + 1 + file.Length;
        if (buffer.Length < need) buffer = new char[Math.Max(need, buffer.Length * 2)];
        folder.CopyTo(buffer);
        var length = folder.Length;
        if (length > 0 && folder[^1] != System.IO.Path.DirectorySeparatorChar && folder[^1] != System.IO.Path.AltDirectorySeparatorChar) buffer[length++] = System.IO.Path.DirectorySeparatorChar;
        file.CopyTo(buffer.AsSpan(length));
        return buffer.AsSpan(0, length + file.Length);
    }
}

/// <summary>A numbered collection of entries to rank. Ranking walks it by index, so a source never has to hold an object per entry.</summary>
internal abstract class ScoutSource
{
    public abstract int Count { get; }
    /// <summary>Describes entry <paramref name="index"/>; false when there is nothing to rank at that place (the entry was removed).</summary>
    public abstract bool TryView(int index, ScoutScratch scratch, out ScoutView view);
    /// <summary>The entry itself, for the few that make it into the results.</summary>
    public abstract ScoutEntry Entry(int index);
    /// <summary>Orders two entries by full path, ignoring case.</summary>
    public abstract int ComparePath(int left, int right, ScoutScratch scratch);
}

internal sealed class ScoutListSource(IReadOnlyList<ScoutEntry> entries) : ScoutSource
{
    public override int Count => entries.Count;
    public override bool TryView(int index, ScoutScratch scratch, out ScoutView view) { view = ScoutView.Of(entries[index]); return true; }
    public override ScoutEntry Entry(int index) => entries[index];
    public override int ComparePath(int left, int right, ScoutScratch scratch) => string.Compare(entries[left].FullPath, entries[right].FullPath, StringComparison.OrdinalIgnoreCase);
}
