using System.Collections.Concurrent;
using System.Text.Json;

namespace TidyWindow.Next.Core;

public sealed record HeldBatch(string Id, DateTimeOffset Created, int Files, long Bytes);
public sealed record RestoreResult(int Restored, int Conflicts, int Missing);

/// <summary>
/// Holds files a rule is unsure about instead of deleting them. A held file is moved, not copied, so holding is instant
/// and frees no space until the batch expires; restoring moves each file back to exactly where it was.
/// An undo folder that cannot be read or written is treated as one with nothing in it: it never stops a scan, a clean or the app.
/// </summary>
public sealed class CleanupQuarantine(string folder, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.Now);

    public string Folder { get; } = folder;

    public Batch Begin()
    {
        var id = $"{clock():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
        return new Batch(id, Path.Combine(Folder, id), clock());
    }

    /// <summary>The newest batch that still has files to restore.</summary>
    public HeldBatch? Latest() => List().OrderByDescending(x => x.Created).FirstOrDefault();

    public IReadOnlyList<HeldBatch> List()
    {
        var batches = new List<HeldBatch>();
        try
        {
            if (!Directory.Exists(Folder)) return batches;
            foreach (var directory in Directory.EnumerateDirectories(Folder))
            {
                var manifest = Read(directory);
                if (manifest is null) continue;
                // Only files that are still held count: a batch whose files have gone has nothing left to put back.
                var held = manifest.Entries.Where(x => File.Exists(x.Stored)).ToList();
                if (held.Count > 0) batches.Add(new HeldBatch(Path.GetFileName(directory), manifest.Created, held.Count, held.Sum(x => x.Length)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return batches;
    }

    public RestoreResult Restore(string id)
    {
        var directory = Path.Combine(Folder, id);
        var manifest = Read(directory);
        if (manifest is null) return new RestoreResult(0, 0, 0);
        int restored = 0, conflicts = 0, missing = 0;
        foreach (var entry in manifest.Entries)
        {
            try
            {
                if (!File.Exists(entry.Stored)) { missing++; continue; }
                if (File.Exists(entry.Original)) { conflicts++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Original)!);
                File.Move(entry.Stored, entry.Original);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { conflicts++; }
        }
        // Whatever could not go back stays held; the batch is gone only once it is empty.
        if (conflicts == 0) TryDelete(directory);
        else
        {
            // A list that cannot be written now is trimmed next time: a file that went back is no longer held, so it is never offered twice.
            try { Write(directory, manifest with { Entries = manifest.Entries.Where(x => File.Exists(x.Stored)).ToList() }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new RestoreResult(restored, conflicts, missing);
    }

    /// <summary>Permanently removes batches older than the retention period.</summary>
    public int Purge()
    {
        var purged = 0;
        try
        {
            if (!Directory.Exists(Folder)) return 0;
            foreach (var directory in Directory.EnumerateDirectories(Folder))
            {
                var manifest = Read(directory);
                var created = manifest?.Created ?? Directory.GetCreationTime(directory);
                if (clock() - created < Retention) continue;
                if (TryDelete(directory)) purged++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return purged;
    }

    private static bool TryDelete(string directory)
    {
        try { Directory.Delete(directory, true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static Manifest? Read(string directory)
    {
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Write(string directory, Manifest manifest)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
    }

    public sealed record Entry(string Original, string Stored, long Length);
    public sealed record Manifest(DateTimeOffset Created, List<Entry> Entries);

    public sealed class Batch(string id, string directory, DateTimeOffset created)
    {
        private readonly ConcurrentBag<Entry> held = [];
        public string Id { get; } = id;
        public int Count => held.Count;
        public long Bytes => held.Sum(x => x.Length);

        /// <summary>Moves one file into the batch, keeping its place under <paramref name="root"/> so it can be put back.</summary>
        /// <exception cref="IOException">The file could not be moved: it is open, the undo folder could not be made, or the drive it is on is full.</exception>
        public void Hold(string ruleId, int rootIndex, string root, string file, long length)
        {
            var relative = Path.GetRelativePath(root, file);
            var stored = Path.Combine(directory, ruleId, rootIndex.ToString(), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
            // A read-only installer cannot be moved to another drive (the move copies it, then deletes the original), so the flag is lifted for the move and put back on the held copy.
            var attributes = File.GetAttributes(file);
            var readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
            if (readOnly) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            var existed = File.Exists(stored);
            try { File.Move(file, stored); }
            catch
            {
                // A move to another drive copies first. A copy that did not finish (the drive filled up, the original was locked part-way) is not kept:
                // it is not in the list of held files, so it could never be restored, and it would only sit in the undo folder taking space.
                if (!existed && File.Exists(file) && File.Exists(stored)) { try { File.Delete(stored); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                if (readOnly && File.Exists(file)) File.SetAttributes(file, attributes);
                throw;
            }
            if (readOnly) File.SetAttributes(stored, attributes);
            held.Add(new Entry(file, stored, length));
        }

        /// <summary>Records what was held. Called once, after every file has been moved. A list that cannot be written leaves the held files where they are, so nothing is lost twice.</summary>
        public void Commit()
        {
            if (held.IsEmpty) { TryDelete(directory); return; }
            try { Write(directory, new Manifest(created, held.ToList())); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
