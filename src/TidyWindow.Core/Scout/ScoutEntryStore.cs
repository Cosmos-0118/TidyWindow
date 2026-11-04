using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace TidyWindow.Next.Core;

/// <summary>
/// The entries of the folder index, kept compactly. A file or folder is a 32-byte record and its name is copied once into a shared block of text; the folder it is in
/// is stored once for all of its entries. That is a fifth of what an object and a full path string for each entry cost, and the garbage collector has nothing
/// to follow in it, so a folder of hundreds of thousands of entries no longer slows every collection or fills the memory of the app.
/// A full path is made only for the few entries that are shown. Entries are only ever added: one that changes or goes away is marked, so a search can read
/// a <see cref="Snapshot"/> while the index is still being filled. Every change is made by one caller at a time (the folder index holds its lock).
/// </summary>
internal sealed class ScoutEntryStore
{
    internal const int Folder = 1, Reparse = 2, HasSize = 4, HasModified = 8, Ascii = 16, Dead = 32;
    private const int SlotShift = 12, SlotsPerChunk = 1 << SlotShift, SlotMask = SlotsPerChunk - 1;
    private const int NameShift = 16, NameChunkChars = 1 << NameShift, NameMask = NameChunkChars - 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Slot
    {
        public int Directory;
        /// <summary>Where the name starts: the block it is in, and its place in that block.</summary>
        public int Name;
        public int NameLength;
        public int Flags;
        public long Size;
        /// <summary>When it was last written, in UTC ticks.</summary>
        public long Modified;
    }

    /// <summary>A folder that holds entries. Its path is kept once, however many entries it has.</summary>
    internal sealed class DirectoryNode(string path)
    {
        public volatile string Path = path;
        /// <summary>Set when the folder is gone, which removes everything in it from searches.</summary>
        public volatile bool Gone;
    }

    private Slot[][] slotChunks = [];
    private char[][] nameChunks = [];
    private int nameUsed = NameChunkChars;
    private int slotCount;
    private int live;
    private DirectoryNode[] directories = new DirectoryNode[64];
    private int directoryCount;
    private readonly Dictionary<string, int> directoryIds = new(StringComparer.OrdinalIgnoreCase);
    // Finds an entry by its folder and name. Only built once something has to be changed after the fact, which a plain walk never needs.
    private int[]? table;
    private int tableUsed;

    /// <summary>How many entries are in the index.</summary>
    public int Live => live;

    /// <summary>A stable view of what is in the store now, safe to read while entries are still being added.</summary>
    public Snapshot Capture() => new(slotChunks, nameChunks, directories, slotCount, live);

    public int RegisterDirectory(string path)
    {
        if (directoryIds.TryGetValue(path, out var known)) return known;
        if (directoryCount == directories.Length) Array.Resize(ref directories, directories.Length * 2);
        directories[directoryCount] = new(path);
        directoryIds[path] = directoryCount;
        return directoryCount++;
    }

    /// <summary>Adds entry <paramref name="i"/> of a batch. One already at that place is replaced.</summary>
    public void Add(ScoutBatch batch, int i)
    {
        var name = batch.NameOf(i);
        ref readonly var entry = ref batch.Slots[i];
        if (table is not null && Find(entry.Directory, name) is var existing and >= 0) Kill(existing);
        var index = Append(entry.Directory, name, entry.Flags, entry.Size, entry.Modified);
        if (table is not null) IndexAdd(index);
    }

    public bool Contains(string path)
    {
        if (!Split(path, out var parent, out var name) || !directoryIds.TryGetValue(parent, out var directory)) return false;
        EnsureIndex();
        return Find(directory, name) >= 0;
    }

    /// <summary>Sets what is known about one path, adding it if it is new.</summary>
    public void Upsert(string path, bool folder, long? size, long modified, bool reparse)
    {
        if (!Split(path, out var parent, out var name)) return;
        var directory = RegisterDirectory(parent);
        EnsureIndex();
        if (Find(directory, name) is var existing and >= 0) Kill(existing);
        var flags = HasModified | (folder ? Folder : 0) | (reparse ? Reparse : 0) | (size is null ? 0 : HasSize) | (System.Text.Ascii.IsValid(name) ? Ascii : 0);
        IndexAdd(Append(directory, name, flags, size ?? 0, modified));
    }

    /// <summary>Takes a path out, and everything inside it when it is a folder.</summary>
    public void RemoveTree(string path)
    {
        EnsureIndex();
        if (Split(path, out var parent, out var name) && directoryIds.TryGetValue(parent, out var directory) && Find(directory, name) is var found and >= 0) Kill(found);
        var inside = path + Path.DirectorySeparatorChar;
        var removed = false;
        for (var i = 0; i < directoryCount; i++)
        {
            var node = directories[i];
            if (node.Gone || !node.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && !node.Path.StartsWith(inside, StringComparison.OrdinalIgnoreCase)) continue;
            node.Gone = true; directoryIds.Remove(node.Path); removed = true;
        }
        if (!removed) return;
        for (var i = 0; i < slotCount; i++)
            if ((At(i).Flags & Dead) == 0 && directories[At(i).Directory].Gone) Kill(i);
    }

    /// <summary>Follows a rename or move: the entry takes its new place, and when it is a folder everything in it comes along.</summary>
    public void Move(string from, string to)
    {
        if (Split(from, out var oldParent, out var oldName) && directoryIds.TryGetValue(oldParent, out var oldDirectory) && Split(to, out var newParent, out var newName))
        {
            EnsureIndex();
            if (Find(oldDirectory, oldName) is var moved and >= 0)
            {
                var old = At(moved);
                Kill(moved);
                var directory = RegisterDirectory(newParent);
                if (Find(directory, newName) is var existing and >= 0) Kill(existing);
                IndexAdd(Append(directory, newName, old.Flags & ~Dead, old.Size, old.Modified));
            }
        }
        // The entries inside a moved folder keep the folder they were in; only what that folder is called changes.
        var inside = from + Path.DirectorySeparatorChar;
        for (var i = 0; i < directoryCount; i++)
        {
            var node = directories[i];
            if (node.Gone) continue;
            var renamed = node.Path.Equals(from, StringComparison.OrdinalIgnoreCase) ? to : node.Path.StartsWith(inside, StringComparison.OrdinalIgnoreCase) ? to + node.Path[from.Length..] : null;
            if (renamed is null) continue;
            directoryIds.Remove(node.Path); node.Path = renamed; directoryIds[renamed] = i;
        }
    }

    private int Append(int directory, ReadOnlySpan<char> name, int flags, long size, long modified)
    {
        if (name.Length > NameChunkChars) name = name[..NameChunkChars];
        if (slotCount >> SlotShift == slotChunks.Length)
        {
            Array.Resize(ref slotChunks, slotChunks.Length + 1);
            slotChunks[^1] = new Slot[SlotsPerChunk];
        }
        if (nameUsed + name.Length > NameChunkChars)
        {
            Array.Resize(ref nameChunks, nameChunks.Length + 1);
            nameChunks[^1] = new char[NameChunkChars];
            nameUsed = 0;
        }
        name.CopyTo(nameChunks[^1].AsSpan(nameUsed));
        ref var slot = ref slotChunks[slotCount >> SlotShift][slotCount & SlotMask];
        slot = new() { Directory = directory, Name = (nameChunks.Length - 1) << NameShift | nameUsed, NameLength = name.Length, Flags = flags, Size = size, Modified = modified };
        nameUsed += name.Length;
        live++;
        return slotCount++;
    }

    private ref Slot At(int index) => ref slotChunks[index >> SlotShift][index & SlotMask];
    private ReadOnlySpan<char> NameOf(in Slot slot) => nameChunks[slot.Name >> NameShift].AsSpan(slot.Name & NameMask, slot.NameLength);

    private void Kill(int index)
    {
        ref var slot = ref At(index);
        if ((slot.Flags & Dead) != 0) return;
        if (table is not null) IndexRemove(index);
        slot.Flags |= Dead;
        live--;
    }

    private static bool Split(string path, out string parent, out string name)
    {
        parent = Path.GetDirectoryName(path)!; name = Path.GetFileName(path);
        return parent is not null && name.Length > 0;
    }

    // The lookup table: open addressing over slot numbers (plus one, so that zero means empty and -1 a removed entry).
    private static int Hash(int directory, ReadOnlySpan<char> name) => HashCode.Combine(directory, string.GetHashCode(name, StringComparison.OrdinalIgnoreCase));

    private void EnsureIndex()
    {
        if (table is null) Rebuild();
    }

    private void Rebuild()
    {
        // Room for at least three times what is in it, so lookups stay short; it is rebuilt, larger, when it is half full.
        var size = 1024;
        while (size < (live + 1) * 3) size <<= 1;
        table = new int[size]; tableUsed = 0;
        for (var i = 0; i < slotCount; i++)
            if ((At(i).Flags & Dead) == 0) IndexAdd(i);
    }

    private void IndexAdd(int index)
    {
        // The rebuilt table already has this entry, because it is in the store before it is indexed.
        if ((tableUsed + 1) * 2 > table!.Length) { Rebuild(); return; }
        ref readonly var slot = ref At(index);
        var mask = table.Length - 1;
        var at = Hash(slot.Directory, NameOf(in slot)) & mask;
        while (table[at] > 0) at = (at + 1) & mask;
        table[at] = index + 1; tableUsed++;
    }

    private void IndexRemove(int index)
    {
        ref readonly var slot = ref At(index);
        var mask = table!.Length - 1;
        for (var at = Hash(slot.Directory, NameOf(in slot)) & mask; table[at] != 0; at = (at + 1) & mask)
            if (table[at] == index + 1) { table[at] = -1; return; }
    }

    private int Find(int directory, ReadOnlySpan<char> name)
    {
        var mask = table!.Length - 1;
        for (var at = Hash(directory, name) & mask; table[at] != 0; at = (at + 1) & mask)
        {
            if (table[at] < 0) continue;
            ref readonly var slot = ref At(table[at] - 1);
            if (slot.Directory == directory && NameOf(in slot).Equals(name, StringComparison.OrdinalIgnoreCase)) return table[at] - 1;
        }
        return -1;
    }

    /// <summary>What a search reads. It holds the blocks as they were when it was taken; later additions go to places it never looks at.</summary>
    internal readonly struct Snapshot(Slot[][] slots, char[][] names, DirectoryNode[] directories, int count, int live)
    {
        public int Count => count;
        public int Live => live;
        public ref readonly Slot At(int index) => ref slots[index >> SlotShift][index & SlotMask];
        public ReadOnlySpan<char> NameOf(in Slot slot) => names[slot.Name >> NameShift].AsSpan(slot.Name & NameMask, slot.NameLength);
        public DirectoryNode DirectoryOf(in Slot slot) => directories[slot.Directory];
    }
}

/// <summary>Entries a walker has found, held until they are handed to the store in one go. A walker fills one of these without making an object or a string per entry.</summary>
internal sealed class ScoutBatch
{
    private ScoutEntryStore.Slot[] slots = new ScoutEntryStore.Slot[512];
    private char[] names = new char[16384];
    private int used;
    public int Count { get; private set; }
    public ScoutEntryStore.Slot[] Slots => slots;

    /// <summary>Records what the walker is looking at and returns its place in the batch.</summary>
    public int Add(int directory, ref FileSystemEntry entry)
    {
        var name = entry.FileName;
        if (Count == slots.Length) Array.Resize(ref slots, Count * 2);
        if (used + name.Length > names.Length) Array.Resize(ref names, Math.Max(names.Length * 2, used + name.Length));
        name.CopyTo(names.AsSpan(used));
        var folder = entry.IsDirectory;
        // Only a folder with the reparse attribute is asked whether it is a link, one extra read each. A OneDrive placeholder has the attribute but no link target, and is walked like any folder.
        var reparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0 && (!folder || ScoutFolderIndex.IsLink(entry.ToFileSystemInfo()));
        slots[Count] = new()
        {
            Directory = directory, Name = used, NameLength = name.Length, Size = folder ? 0 : entry.Length, Modified = entry.LastWriteTimeUtc.UtcTicks,
            Flags = ScoutEntryStore.HasModified | (folder ? ScoutEntryStore.Folder : ScoutEntryStore.HasSize) | (reparse ? ScoutEntryStore.Reparse : 0)
                | (System.Text.Ascii.IsValid(name) ? ScoutEntryStore.Ascii : 0)
        };
        used += name.Length;
        return Count++;
    }

    public ReadOnlySpan<char> NameOf(int index) => names.AsSpan(slots[index].Name, slots[index].NameLength);
    /// <summary>A folder that is not a link, which is the kind a walker goes into.</summary>
    public bool IsWalkable(int index) => (slots[index].Flags & (ScoutEntryStore.Folder | ScoutEntryStore.Reparse)) == ScoutEntryStore.Folder;
    public void Clear() { Count = 0; used = 0; }
}

internal sealed class ScoutStoreSource(ScoutEntryStore.Snapshot snapshot) : ScoutSource
{
    public override int Count => snapshot.Count;

    public override bool TryView(int index, ScoutScratch scratch, out ScoutView view)
    {
        ref readonly var slot = ref snapshot.At(index);
        var flags = slot.Flags;
        var directory = snapshot.DirectoryOf(in slot);
        if ((flags & ScoutEntryStore.Dead) != 0 || directory.Gone) { view = default; return false; }
        var name = snapshot.NameOf(in slot);
        // Plain ASCII names are normalized by lowercasing; any other name takes the full treatment.
        view = new(directory.Path, scratch, name, (flags & ScoutEntryStore.Ascii) != 0 ? scratch.Lower(name) : ScoutQuery.Normalize(name.ToString()),
            (flags & ScoutEntryStore.Folder) != 0, (flags & ScoutEntryStore.HasSize) != 0 ? slot.Size : null,
            (flags & ScoutEntryStore.HasModified) != 0 ? new DateTimeOffset(slot.Modified, TimeSpan.Zero) : null);
        return true;
    }

    public override ScoutEntry Entry(int index)
    {
        ref readonly var slot = ref snapshot.At(index);
        var flags = slot.Flags;
        return new(Path.Join(snapshot.DirectoryOf(in slot).Path, snapshot.NameOf(in slot).ToString()), (flags & ScoutEntryStore.Folder) != 0,
            (flags & ScoutEntryStore.HasSize) != 0 ? slot.Size : null, (flags & ScoutEntryStore.HasModified) != 0 ? new DateTimeOffset(slot.Modified, TimeSpan.Zero) : null,
            (flags & ScoutEntryStore.Reparse) != 0);
    }

    public override int ComparePath(int left, int right, ScoutScratch scratch)
    {
        ref readonly var a = ref snapshot.At(left); ref readonly var b = ref snapshot.At(right);
        return scratch.Left(snapshot.DirectoryOf(in a).Path, snapshot.NameOf(in a)).CompareTo(scratch.Right(snapshot.DirectoryOf(in b).Path, snapshot.NameOf(in b)), StringComparison.OrdinalIgnoreCase);
    }
}
