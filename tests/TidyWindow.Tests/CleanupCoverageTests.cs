using System.Text;
using System.Text.RegularExpressions;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

/// <summary>
/// Measures how much of the junk that really sits on this PC the Cleanup page finds. The reference list is built without the
/// catalog: Windows' own Disk Cleanup registry list, a fixed table of well-known cache folders, and a name based sweep for
/// cache-like folders in the profile. Opt in with TIDYWINDOW_LIVE_TESTS=1; set TIDYWINDOW_COVERAGE_REPORT to a file to keep the table.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CleanupCoverageTests(ITestOutputHelper output)
{
    private const double MinimumByteCoverage = 0.90;
    private const double MinimumLocationCoverage = 0.90;
    private const long BigLocation = 50L * 1024 * 1024;

    private sealed record Place(string Path, string Source, long Bytes, long Covered, long CoveredRecommended);

    // Folders that look like caches by name but hold things the user or an app would miss, so they are not reclaimable space.
    private static readonly string[] NotJunk =
    [
        @"\Packages\*\LocalCache",   // Store apps keep real data here as well as cache
        @"\Package Cache",           // installer sources Windows needs for repair and uninstall
        @"\.cache",                  // tool runtimes and downloaded models
        @"\Documents\",              // the user's projects
        @"\node_modules\",
        @"\Downloads",
        @"\Microsoft\Windows\WebCache" // locked index database
    ];

    private static readonly Regex CacheLike = new(@"^(cache|caches|code cache|gpucache|grshadercache|shadercache|dawn.*cache|crashpad|crashdumps|crash reports|logs?|temp|cache2|startupcache|cachestorage|cache_data|cacheddata|cachedextensionvsix.*|media ?cache|blob_storage|component_crx_cache|webcache.*|htmlcache|d3dscache|dxcache|glcache|http-cache|browsercache|.*[-_]cache)$", RegexOptions.IgnoreCase);

    [Fact]
    public void TheCatalogFindsMostOfTheReclaimableSpaceOnThisPc()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        var context = CleanupContext.Current;
        var rules = CleanupCatalog.Load();
        var plan = CleanupPaths.Plan(rules, context);
        string[] Roots(Func<CleanupRule, bool> keep) => plan.Where((_, i) => keep(rules[i])).SelectMany(x => x).Select(CleanupPaths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var places = Measure(Reference(context), Roots(_ => true), Roots(rule => rule.Level == CleanupLevel.Recommended), rules);

        var total = places.Sum(x => x.Bytes);
        var covered = places.Sum(x => x.Covered);
        var recommended = places.Sum(x => x.CoveredRecommended);
        var big = places.Where(x => x.Bytes >= BigLocation).ToList();
        var bigCovered = big.Count(x => x.Covered * 2 >= x.Bytes);
        var report = new StringBuilder();
        report.AppendLine($"Reference locations: {places.Count}, {Gb(total)} GB reclaimable; the catalog reaches {Gb(covered)} GB = {covered * 100.0 / Math.Max(1, total):F1}%");
        report.AppendLine($"Of that, found by the preselected (Recommended) rules alone: {Gb(recommended)} GB = {recommended * 100.0 / Math.Max(1, total):F1}%");
        report.AppendLine($"Locations of 50 MB or more: {big.Count}; reached: {bigCovered} = {bigCovered * 100.0 / Math.Max(1, big.Count):F1}%");
        report.AppendLine().AppendLine("Gaps, biggest first:");
        foreach (var place in places.Where(x => x.Covered < x.Bytes).OrderByDescending(x => x.Bytes - x.Covered).Take(40))
            report.AppendLine($"  {(place.Bytes - place.Covered) / 1048576,7} MB  {place.Path}  [{place.Source}]");
        output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_COVERAGE_REPORT") is { Length: > 0 } file) File.WriteAllText(file, report.ToString());

        Assert.True(covered >= total * MinimumByteCoverage, $"Only {covered * 100.0 / Math.Max(1, total):F1}% of reclaimable bytes are covered.\n{report}");
        Assert.True(big.Count == 0 || bigCovered >= big.Count * MinimumLocationCoverage, $"Only {bigCovered}/{big.Count} large locations are covered.\n{report}");
    }

    [Fact]
    public async Task EveryRuleScansWithoutErrorsOnThisPc()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        var rules = CleanupCatalog.Load();
        var findings = await new CleanupScanner(CleanupContext.Current).ScanAsync(rules);
        Assert.Equal(rules.Count, findings.Count);
        foreach (var finding in findings.Where(x => x.HasContent)) output.WriteLine($"{finding.Rule.Id,-28} {finding.Bytes / 1048576,8} MB {finding.Files,7} files  admin={finding.NeedsAdmin} unreadable={finding.Unreadable}");
        foreach (var finding in findings.Where(x => x.Rule.Discover is not null || x.Rule.Handlers.Length > 0))
            foreach (var root in finding.Roots) output.WriteLine($"{finding.Rule.Id}: {root}");
        Assert.All(findings, finding => Assert.True(finding.Bytes >= 0));
    }

    [Fact]
    public async Task ScannedSizesMatchAnIndependentCountAndNothingIsCountedTwiceOrUnsafe()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        var context = CleanupContext.Current;
        var rules = CleanupCatalog.Load();
        var findings = await new CleanupScanner(context).ScanAsync(rules);
        var protectedPaths = CleanupPaths.Protected(context);
        var seen = new List<string>();
        foreach (var finding in findings.Where(x => x.Roots.Length > 0))
        {
            var rule = finding.Rule;
            foreach (var root in finding.Roots)
            {
                Assert.True(CleanupPaths.IsSafeRoot(root, rule, protectedPaths), $"{rule.Id}: {root} is not a safe root");
                Assert.True(CleanupPaths.IsPlainFolder(root), $"{rule.Id}: {root} is a link or missing");
                if (rule.Masks.Length == 0 && rule.Recurse)
                {
                    Assert.DoesNotContain(seen, other => other.Equals(root, StringComparison.OrdinalIgnoreCase) || CleanupPaths.IsInside(root, other) || CleanupPaths.IsInside(other, root));
                    seen.Add(root);
                }
            }
            // Rules without age or file filters must match a plain recursive count of their folders exactly (files appear and vanish, so allow a little drift).
            if (rule.Masks.Length == 0 && rule.MinAgeDays == 0 && rule.Recurse && rule.Discover is null)
            {
                var independent = finding.Roots.Sum(root => Size(root).Bytes);
                output.WriteLine($"{rule.Id,-26} scanner {finding.Bytes,14:N0}  independent {independent,14:N0}");
                Assert.InRange(finding.Bytes, independent - Math.Max(1_000_000, independent / 50), independent + Math.Max(1_000_000, independent / 50));
            }
        }
        var chromium = findings.First(x => x.Rule.Discover is not null);
        var allowed = new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GraphiteDawnCache", "GrShaderCache", "ShaderCache", "component_crx_cache", "extensions_crx_cache", "Crashpad", "CacheStorage", "ScriptCache" };
        Assert.All(chromium.Roots, root => Assert.Contains(Path.GetFileName(root), allowed, StringComparer.OrdinalIgnoreCase));
    }

    // Cleans copies of real cache folders from this PC (never the originals), so real file shapes are exercised: read-only files, odd names, deep trees.
    [Fact]
    public async Task ACleanOfCopiedRealFoldersFreesWhatTheScanPromisedAndNothingElse()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        var real = CleanupContext.Current;
        var sandbox = Path.Combine(Path.GetTempPath(), "tw-live-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var local = Path.Combine(sandbox, "Local"); var roaming = Path.Combine(sandbox, "Roaming"); var profile = Path.Combine(sandbox, "Profile");
            Directory.CreateDirectory(local); Directory.CreateDirectory(roaming); Directory.CreateDirectory(profile);
            var realLocal = real.Variable("LOCALAPPDATA")!; var realRoaming = real.Variable("APPDATA")!;
            var copied = 0L;
            foreach (var (from, to) in new[] { (Path.Combine(realRoaming, "discord"), Path.Combine(roaming, "discord")), (Path.Combine(realRoaming, "Riot Client"), Path.Combine(roaming, "Riot Client")), (Path.Combine(realLocal, "Microsoft", "Windows", "INetCache"), Path.Combine(local, "Microsoft", "Windows", "INetCache")) })
                if (Directory.Exists(from)) copied += Copy(from, to);
            output.WriteLine($"Copied {copied:N0} bytes of real data into the sandbox");
            if (copied == 0) return;
            var keepers = Directory.EnumerateFiles(roaming, "*", SearchOption.AllDirectories).Where(x => Path.GetFileName(x) is "Local State" or "Cookies" or "Preferences").ToList();
            var context = new CleanupContext
            {
                Variable = name => name switch { "LOCALAPPDATA" => local, "APPDATA" => roaming, "USERPROFILE" => profile, "TEMP" => Path.Combine(local, "Temp"), "WINDIR" => real.Variable("WINDIR"), "PROGRAMDATA" => real.Variable("PROGRAMDATA"), _ => null },
                IsAdministrator = false, Parallelism = 8, HandlerFolders = _ => null
            };
            var rules = CleanupCatalog.Load().Where(x => x.Id is "chromium-apps" or "web-cache").ToList();
            var findings = (await new CleanupScanner(context).ScanAsync(rules)).Where(x => x.HasContent).ToList();
            var promised = findings.Sum(x => x.Bytes);
            var result = await new CleanupCleaner(context).CleanAsync(findings);
            var after = (await new CleanupScanner(context).ScanAsync(rules)).Sum(x => x.Bytes);
            output.WriteLine($"Scan promised {promised:N0}; clean freed {result.FreedBytes:N0} ({result.Removed} files, {result.InUse} in use, {result.Denied} denied, {result.Failed} failed); rescan finds {after:N0}");
            Assert.True(promised > 0);
            Assert.Equal(promised, result.FreedBytes);
            Assert.Equal(0, after);
            Assert.All(keepers, file => Assert.True(File.Exists(file), $"{file} was removed"));
        }
        finally { try { Directory.Delete(sandbox, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static long Copy(string from, string to)
    {
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(from).EnumerateFiles("*", options))
        {
            try
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                file.CopyTo(target, true);
                File.SetAttributes(target, FileAttributes.Normal);
                bytes += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return bytes;
    }

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("F2");

    private static List<Place> Measure(IEnumerable<(string Path, string Source)> reference, string[] roots, string[] recommendedRoots, IReadOnlyList<CleanupRule> rules)
    {
        // Keep only the outermost folders so nothing is counted twice.
        var all = reference.Select(x => (Path: CleanupPaths.Normalize(x.Path), x.Source)).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Path.Length).ToList();
        var outer = new List<(string Path, string Source)>();
        foreach (var item in all)
            if (!outer.Any(parent => CleanupPaths.IsInside(item.Path, parent.Path))) outer.Add(item);

        var places = new System.Collections.Concurrent.ConcurrentBag<Place>();
        Parallel.ForEach(outer, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
        {
            var (bytes, _) = Size(item.Path);
            if (bytes <= 0) return;
            // A catalog folder that holds the place, or sits inside it, reaches that much of it.
            long Reach(string[] from) => from.Any(root => root.Equals(item.Path, StringComparison.OrdinalIgnoreCase) || CleanupPaths.IsInside(item.Path, root))
                ? bytes
                : Math.Min(bytes, from.Where(root => CleanupPaths.IsInside(root, item.Path)).Sum(root => Size(root).Bytes));
            places.Add(new Place(item.Path, item.Source, bytes, Reach(roots), Reach(recommendedRoots)));
        });

        var list = places.ToList();
        // Things that are not folders: the Recycle Bin and the hibernation file are covered by their own actions.
        var (bin, _) = CleanupNativeProbe.RecycleBin();
        if (bin > 0) list.Add(new Place("Recycle Bin", "shell", bin, rules.Any(x => x.Action == CleanupAction.RecycleBin) ? bin : 0, 0));
        var hiberfil = Path.Combine(Environment.GetEnvironmentVariable("SYSTEMDRIVE") + Path.DirectorySeparatorChar, "hiberfil.sys");
        if (File.Exists(hiberfil)) list.Add(new Place(hiberfil, "windows", new FileInfo(hiberfil).Length, rules.Any(x => x.Action == CleanupAction.Hibernation) ? new FileInfo(hiberfil).Length : 0, 0));
        return list;
    }

    private static (long Bytes, int Files) Size(string folder)
    {
        long bytes = 0; var files = 0;
        try
        {
            if (File.Exists(folder)) return (new FileInfo(folder).Length, 1);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options)) { bytes += file.Length; files++; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (bytes, files);
    }

    private static IEnumerable<(string Path, string Source)> Reference(CleanupContext context)
    {
        string? Env(string name) => context.Variable(name);
        var local = Env("LOCALAPPDATA")!; var roaming = Env("APPDATA")!; var profile = Env("USERPROFILE")!; var windows = Env("WINDIR")!; var data = Env("PROGRAMDATA")!;

        // 1. Windows' own Disk Cleanup list: every handler that names a folder.
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches"))
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                if (name == "DownloadsFolder") continue;
                using var handler = key!.OpenSubKey(name);
                foreach (var folder in (handler?.GetValue("Folder") as string)?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [])
                {
                    var expanded = Environment.ExpandEnvironmentVariables(folder);
                    if (expanded.Contains('?') || !Path.IsPathRooted(expanded)) continue;
                    var full = CleanupPaths.Normalize(expanded);
                    // Handlers that point at the Windows folder itself select single files by name; those are not whole-folder targets.
                    if (full.Equals(CleanupPaths.Normalize(windows), StringComparison.OrdinalIgnoreCase)) continue;
                    yield return (full, "disk cleanup: " + name);
                }
            }

        // 2. Well-known cache folders.
        string[] known =
        [
            $@"{windows}\SoftwareDistribution\Download", $@"{windows}\SoftwareDistribution\DeliveryOptimization", $@"{windows}\Prefetch", $@"{windows}\Logs", $@"{windows}\Minidump", $@"{windows}\LiveKernelReports", $@"{windows}\Temp",
            $@"{local}\Microsoft\Windows\INetCache", $@"{local}\Microsoft\Windows\WER", $@"{local}\CrashDumps", $@"{data}\Microsoft\Windows\WER", $@"{data}\USOShared\Logs",
            $@"{local}\npm-cache", $@"{roaming}\npm-cache", $@"{profile}\scoop\cache", $@"{profile}\scoop\persist\nodejs-lts\cache", $@"{local}\Yarn\Cache", $@"{local}\pnpm\store", $@"{local}\pip\Cache", $@"{local}\pypoetry\Cache", $@"{local}\uv\cache",
            $@"{local}\NuGet\v3-cache", $@"{local}\NuGet\plugins-cache", $@"{profile}\.nuget\packages", $@"{profile}\.gradle\caches", $@"{profile}\.m2\repository", $@"{profile}\.cargo\registry", $@"{profile}\.cargo\git", $@"{local}\go-build", $@"{profile}\go\pkg\mod\cache",
            $@"{profile}\.chocolatey\http-cache", $@"{data}\chocolatey\logs", $@"{data}\Microsoft\VisualStudio\Packages", $@"{local}\D3DSCache", $@"{local}\NVIDIA\DXCache", $@"{local}\NVIDIA\GLCache", $@"{data}\NVIDIA Corporation\NV_Cache", $@"{local}\AMD\DxCache", $@"{local}\Steam\htmlcache",
            $@"{local}\Spotify\Data", $@"{local}\Microsoft\OneDrive\logs", $@"{local}\Microsoft\Edge\User Data\component_crx_cache", $@"{local}\LocalLow\Microsoft\CryptnetUrlCache"
        ];
        foreach (var path in known) yield return (path, "known");
        yield return (Path.Combine(Path.GetDirectoryName(local)!, "LocalLow", "Microsoft", "CryptnetUrlCache"), "known");
        yield return (Path.Combine(Path.GetDirectoryName(local)!, "LocalLow", "AMD", "DxCache"), "known");

        // 3. Store apps clear their own TempState and network caches; nothing else in a package is assumed to be cache.
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            foreach (var package in Directory.EnumerateDirectories(packages))
                foreach (var sub in new[] { "TempState", @"AC\INetCache", @"AC\Temp" })
                    yield return (Path.Combine(package, sub), "store app");

        // 4. Anything in the profile whose folder name says it is a cache, log or crash report.
        foreach (var start in new[] { local, roaming, Path.Combine(Path.GetDirectoryName(local)!, "LocalLow"), data, profile })
            foreach (var found in Sweep(start, 0)) yield return (found, "named like a cache");
    }

    private static IEnumerable<string> Sweep(string folder, int depth)
    {
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(folder).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var child in children)
        {
            try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var name = Path.GetFileName(child);
            if (CacheLike.IsMatch(name))
            {
                if (!Excluded(child)) yield return child;
                continue;
            }
            if (depth < 7 && !name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.') || depth < 1)
                foreach (var found in Sweep(child, depth + 1)) yield return found;
        }
    }

    private static bool Excluded(string path)
    {
        foreach (var pattern in NotJunk)
        {
            if (!pattern.Contains('*')) { if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase) || path.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)) return true; continue; }
            var regex = "^.*" + Regex.Escape(pattern).Replace(@"\*", @"[^\\]+") + "$";
            if (Regex.IsMatch(path, regex, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }
}

internal static class CleanupNativeProbe
{
    public static (long Bytes, long Items) RecycleBin() => CleanupNative.QueryRecycleBin();
}
