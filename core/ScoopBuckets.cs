using System.Text.Json;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>A bucket that can be added: where it is, and what it is for. <see cref="Official"/> means it is one of Scoop's own.</summary>
public sealed record BucketOffer(string Name, string Url, string Description, bool Official)
{
    /// <summary>The "owner/repository" part of the address, shown so nobody has to take the name on trust.</summary>
    public string Repository => Url["https://github.com/".Length..];
}

public sealed record AddedBucket(string Name, string Source, int Manifests);
public sealed record BucketOverview(IReadOnlyList<AddedBucket> Added, IReadOnlyList<BucketOffer> Available, bool GitMissing, bool Usable = true)
{
    public static readonly BucketOverview Unavailable = new([], [], false, false);
}
public sealed record BucketAddResult(bool Ok, string Message, string Detail = "");

/// <summary>
/// Shows which Scoop buckets are added and which popular ones can be, and adds one on request. A search only covers the buckets that are added,
/// so this is how a person widens it. Only buckets from the list below can be added, never an address typed in by a caller.
/// </summary>
public sealed partial class ScoopBuckets(PackageService packages, ICommandRunner runner, Func<string, string?>? findGit = null)
{
    // Scoop's own buckets, most useful first. Their addresses come from Scoop's buckets.json when it can be read, because they have moved before.
    private static readonly string[] OfficialOrder = ["extras", "versions", "nonportable", "java", "games", "nirsoft", "sysinternals", "php", "nerd-fonts"];
    private static readonly Dictionary<string, string> OfficialFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main"] = "https://github.com/ScoopInstaller/Main", ["extras"] = "https://github.com/ScoopInstaller/Extras", ["versions"] = "https://github.com/ScoopInstaller/Versions",
        ["nirsoft"] = "https://github.com/ScoopInstaller/Nirsoft", ["sysinternals"] = "https://github.com/ScoopInstaller/Sysinternals", ["php"] = "https://github.com/ScoopInstaller/PHP",
        ["nerd-fonts"] = "https://github.com/matthewjberger/scoop-nerd-fonts", ["nonportable"] = "https://github.com/ScoopInstaller/Nonportable",
        ["java"] = "https://github.com/ScoopInstaller/Java", ["games"] = "https://github.com/ScoopInstaller/Games"
    };
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["extras"] = "Popular desktop apps: browsers, editors, media players and more.",
        ["versions"] = "Older, beta and nightly versions of apps.",
        ["nonportable"] = "Apps that need an installer or administrator rights.",
        ["java"] = "Java runtimes and development kits (OpenJDK, Temurin, Oracle and others).",
        ["games"] = "Free and open-source games and game tools.",
        ["nirsoft"] = "The NirSoft collection of small Windows utilities.",
        ["sysinternals"] = "Microsoft Sysinternals tools such as Process Explorer.",
        ["php"] = "PHP versions and related tools.",
        ["nerd-fonts"] = "Programming fonts patched with extra icons (Nerd Fonts)."
    };

    // Well-known buckets run by volunteers rather than by Scoop. Each was checked to be active, cloneable and openly licensed.
    private static readonly BucketOffer[] Community =
    [
        new("dorado", "https://github.com/chawyehsu/dorado", "A large community bucket of everyday desktop apps.", false),
        new("DEV-tools", "https://github.com/anderlli0053/DEV-tools", "A large community bucket of developer tools and utilities.", false),
        new("spotify", "https://github.com/TheRandomLabs/Scoop-Spotify", "Spotify, Spicetify and related packages.", false)
    ];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex SafeUrl();

    public const string GitNeeded = "Git is needed to add buckets. Install Git from Install apps (search for “git”), then come back.";

    /// <summary>The buckets on this PC and the ones that could be added. Reads files only, so it is quick, but keep it off the thread that draws the screen.</summary>
    public BucketOverview Read(ManagerStatus scoop)
    {
        if (scoop.Definition.Kind != ManagerKind.Scoop || ScoopCatalog.RootFor(scoop.Executable) is not { } root) return BucketOverview.Unavailable;
        var added = new List<AddedBucket>();
        try
        {
            var folder = Path.Combine(root, "buckets");
            if (Directory.Exists(folder))
                foreach (var directory in Directory.EnumerateDirectories(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    added.Add(new(Path.GetFileName(directory), RemoteOf(directory), CountManifests(directory)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // A bucket counts as added under any name if its address matches, so one added by hand is not offered a second time.
        var names = added.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addresses = added.Select(x => Normalize(x.Source)).Where(x => x.Length > 0).ToHashSet();
        var available = Offers(root).Where(x => !names.Contains(x.Name) && !addresses.Contains(Normalize(x.Url))).ToArray();
        return new(added, available, (findGit ?? FindGit)(root) is null);
    }

    public async Task<BucketAddResult> AddAsync(ManagerStatus scoop, string name, CancellationToken token = default)
    {
        if (scoop.Health != ManagerHealth.Ready || scoop.Executable is null || scoop.Definition.Kind != ManagerKind.Scoop)
            return new(false, "Scoop is not ready. Refresh its status first.");
        var overview = await Task.Run(() => Read(scoop), token).ConfigureAwait(false);
        if (overview.Added.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return new(true, $"{name} is already added.");
        var offer = overview.Available.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (offer is null) return new(false, "That bucket is not in the list, so it was not added.");
        if (overview.GitMissing) return new(false, GitNeeded);
        if (!await packages.OperationGate.WaitAsync(0, token).ConfigureAwait(false)) return new(false, "An installation or setup is running. Try again when it finishes.");
        try
        {
            // Cloning a large bucket can take a while on a slow connection.
            var command = packages.BuildCommand(ManagerKind.Scoop, scoop.Executable, ["bucket", "add", offer.Name, offer.Url], TimeSpan.FromMinutes(5));
            CommandResult result;
            try { result = await runner.RunAsync(command, token).ConfigureAwait(false); }
            catch (TimeoutException) { return new(false, $"Adding {offer.Name} took too long. Check your connection and try again."); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, $"{offer.Name} could not be added.", ex.Message); }

            // Judge by what is on disk, not by Scoop's exit code alone.
            var now = (await Task.Run(() => Read(scoop), token).ConfigureAwait(false)).Added.FirstOrDefault(x => string.Equals(x.Name, offer.Name, StringComparison.OrdinalIgnoreCase));
            if (result.ExitCode == 0 && now is { Manifests: > 0 })
            {
                packages.ClearSearchCache();
                return new(true, $"Added {offer.Name}. Its {now.Manifests:N0} apps now show up when you search.");
            }
            return new(false, $"{offer.Name} could not be added.", Detail(result));
        }
        finally { packages.OperationGate.Release(); }
    }

    private static string Detail(CommandResult result)
    {
        var text = PackageOutputParser.Clean(string.Join("\n", new[] { result.Error, result.Output }.Where(x => !string.IsNullOrWhiteSpace(x)))).Trim();
        return text.Length > 1000 ? text[^1000..] : text;
    }

    private static IReadOnlyList<BucketOffer> Offers(string root)
    {
        var official = new Dictionary<string, string>(OfficialFallback, StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(root, "apps", "scoop", "current", "buckets.json");
            if (File.Exists(file))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(file));
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var entry in document.RootElement.EnumerateObject())
                        if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { } url) official[entry.Name] = url;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        var offers = new List<BucketOffer>();
        // The main bucket is not offered: Scoop cannot work without it, and it is added with Scoop.
        foreach (var name in OfficialOrder.Concat(official.Keys.Where(x => !x.Equals("main", StringComparison.OrdinalIgnoreCase) && !OfficialOrder.Contains(x, StringComparer.OrdinalIgnoreCase))))
            if (official.TryGetValue(name, out var url) && SafeName().IsMatch(name) && SafeUrl().IsMatch(url))
                offers.Add(new(name, url, Descriptions.GetValueOrDefault(name, "An official Scoop bucket."), true));
        offers.AddRange(Community);
        return offers;
    }

    /// <summary>The address a bucket was cloned from, read from its git settings. Empty when it cannot be told.</summary>
    internal static string RemoteOf(string bucketDirectory)
    {
        try
        {
            var config = Path.Combine(bucketDirectory, ".git", "config");
            if (!File.Exists(config)) return "";
            var inOrigin = false;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inOrigin = line.Replace(" ", "").Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase); continue; }
                if (inOrigin && line.StartsWith("url", StringComparison.OrdinalIgnoreCase) && line.IndexOf('=') is var equals and > 0) return line[(equals + 1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "";
    }

    private static string Normalize(string url)
    {
        var text = url.Trim().TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        return text.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    }

    private static int CountManifests(string bucketDirectory)
    {
        var manifests = ScoopCatalog.ManifestFolder(bucketDirectory);
        return Directory.EnumerateFiles(manifests, "*.json", manifests == bucketDirectory ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
            .Count(x => !x.Contains(@"\deprecated\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where Scoop would find git: on the PATH, in Scoop's own shims, or in its Git app.</summary>
    internal static string? FindGit(string root)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Concat([Path.Combine(root, "shims"), Path.Combine(root, "apps", "git", "current", "cmd")]);
        foreach (var folder in folders)
        {
            try { if (File.Exists(Path.Combine(folder.Trim().Trim('"'), "git.exe"))) return Path.Combine(folder.Trim().Trim('"'), "git.exe"); }
            catch (ArgumentException) { }
        }
        return null;
    }
}
