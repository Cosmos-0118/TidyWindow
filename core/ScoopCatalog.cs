using System.Collections.Concurrent;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>
/// Searches the Scoop buckets already on disk without starting Scoop. <c>scoop search</c> parses thousands of manifests in PowerShell on every call
/// (up to 13 seconds here); reading them once and keeping them in memory makes a search instant.
/// </summary>
public sealed class ScoopCatalog
{
    private sealed record Entry(string Name, string Version, string[] Binaries);
    private sealed record Bucket(string Name, string Stamp, Entry[] Entries);

    private readonly object gate = new();
    private readonly Dictionary<string, Bucket> buckets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scoop's folder: next to the <c>shims</c> folder the executable lives in, else <c>SCOOP</c>, else the default. Null when none of them has buckets.</summary>
    public static string? RootFor(string? executable)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(executable) && Path.GetDirectoryName(executable) is { } shims &&
            string.Equals(Path.GetFileName(shims), "shims", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(shims) is { } beside) candidates.Add(beside);
        if (Environment.GetEnvironmentVariable("SCOOP") is { Length: > 0 } custom) candidates.Add(custom);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop"));
        return candidates.FirstOrDefault(x => Directory.Exists(Path.Combine(x, "buckets")));
    }

    /// <summary>Where a bucket keeps its manifests: its <c>bucket</c> folder, or the bucket's own folder when it has none.</summary>
    internal static string ManifestFolder(string bucketDirectory) => Directory.Exists(Path.Combine(bucketDirectory, "bucket")) ? Path.Combine(bucketDirectory, "bucket") : bucketDirectory;

    /// <summary>The matching packages, best first, or null when the buckets cannot be read so the caller should ask Scoop itself.</summary>
    public IReadOnlyList<Package>? Search(string root, string query, int limit = 60, CancellationToken token = default)
    {
        IReadOnlyList<Bucket> all;
        try { all = Load(root, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (all.Count == 0) return null;

        // Like `scoop search`, a package matches on its name or on a program it provides; a "bucket/name" query matches the full id.
        var text = query.Trim();
        var byId = text.Contains('/');
        var matches = new List<(int Rank, Bucket Bucket, Entry Entry)>();
        foreach (var bucket in all)
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in bucket.Entries)
            {
                var rank = byId ? ($"{bucket.Name}/{entry.Name}".Contains(text, StringComparison.OrdinalIgnoreCase) ? 0 : -1) : Rank(entry, text);
                if (rank >= 0) matches.Add((rank, bucket, entry));
            }
        }
        return matches.OrderBy(x => x.Rank).ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Bucket.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Package(ManagerKind.Scoop, $"{x.Bucket.Name}/{x.Entry.Name}", x.Entry.Name, x.Entry.Version, x.Bucket.Name))
            .Where(x => PackageOutputParser.IsSafeId(x.Id))
            .DistinctBy(x => x.Key).Take(limit).ToArray();
    }

    /// <summary>Reads the buckets now, so the first real search does not have to.</summary>
    public void Warm(string root, CancellationToken token = default)
    {
        try { Load(root, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static int Rank(Entry entry, string text)
    {
        if (entry.Name.Equals(text, StringComparison.OrdinalIgnoreCase)) return 0;
        if (entry.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 1;
        if (entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return 2;
        if (entry.Binaries.Any(x => x.Equals(text, StringComparison.OrdinalIgnoreCase))) return 3;
        return entry.Binaries.Any(x => x.Contains(text, StringComparison.OrdinalIgnoreCase)) ? 4 : -1;
    }

    private IReadOnlyList<Bucket> Load(string root, CancellationToken token)
    {
        var folder = Path.Combine(root, "buckets");
        if (!Directory.Exists(folder)) return [];
        // One search builds the index; one that arrives meanwhile waits for it instead of reading everything a second time.
        lock (gate)
        {
            var current = new List<Bucket>();
            foreach (var directory in Directory.EnumerateDirectories(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var manifests = ManifestFolder(directory);
                var stamp = Stamp(directory, manifests);
                if (!buckets.TryGetValue(directory, out var known) || known.Stamp != stamp)
                    buckets[directory] = known = new(Path.GetFileName(directory), stamp, ReadBucket(manifests, recurse: manifests != directory, token));
                current.Add(known);
            }
            return current;
        }
    }

    // A bucket is updated with git, which rewrites its index; adding or removing a manifest changes the folder's own time.
    private static string Stamp(string bucket, string manifests) =>
        $"{Directory.GetLastWriteTimeUtc(manifests).Ticks}|{File.GetLastWriteTimeUtc(Path.Combine(bucket, ".git", "index")).Ticks}|{File.GetLastWriteTimeUtc(Path.Combine(bucket, ".git", "HEAD")).Ticks}";

    private static Entry[] ReadBucket(string manifests, bool recurse, CancellationToken token)
    {
        var files = Directory.EnumerateFiles(manifests, "*.json", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(x => !x.Contains(@"\deprecated\", StringComparison.OrdinalIgnoreCase)).ToArray();
        var found = new ConcurrentBag<Entry>();
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = token }, file => { if (Read(file) is { } entry) found.Add(entry); });
        return [.. found.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static Entry? Read(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var versionElement)) return null;
            var version = versionElement.ValueKind switch { JsonValueKind.String => versionElement.GetString() ?? "", JsonValueKind.Number => versionElement.GetRawText(), _ => "" };
            if (string.IsNullOrWhiteSpace(version)) return null;
            var binaries = new List<string>();
            Collect(root, binaries);
            if (root.TryGetProperty("architecture", out var architectures) && architectures.ValueKind == JsonValueKind.Object)
                foreach (var architecture in architectures.EnumerateObject()) if (architecture.Value.ValueKind == JsonValueKind.Object) Collect(architecture.Value, binaries);
            return new(Path.GetFileNameWithoutExtension(file), version.Trim(), [.. binaries.Distinct(StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>The programs a manifest puts on the PATH: a file, a list of files, or files with a different name to run them by ("tool.exe", ["tool.exe", "alias"]).</summary>
    private static void Collect(JsonElement owner, List<string> into)
    {
        if (!owner.TryGetProperty("bin", out var bin)) return;
        void Add(string? path) { if (!string.IsNullOrWhiteSpace(path) && Path.GetFileNameWithoutExtension(path.Replace('/', '\\')) is { Length: > 0 } name) into.Add(name); }
        switch (bin.ValueKind)
        {
            case JsonValueKind.String: Add(bin.GetString()); break;
            case JsonValueKind.Array:
                foreach (var item in bin.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) Add(item.GetString());
                    else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0)
                        Add(item.GetArrayLength() > 1 && item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : null);
                }
                break;
        }
    }
}
