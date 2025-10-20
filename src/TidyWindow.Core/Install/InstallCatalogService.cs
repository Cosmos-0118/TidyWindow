using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TidyWindow.Core.Install;

public sealed class InstallCatalogService
{
    private readonly object _snapshotLock = new();
    private Lazy<CatalogSnapshot> _snapshot;

    public InstallCatalogService()
    {
        _snapshot = CreateSnapshotFactory();
    }

    public string CatalogPath => GetSnapshot().SourcePath;

    public IReadOnlyList<InstallBundleDefinition> Bundles => GetSnapshot().Bundles;

    public IReadOnlyList<InstallPackageDefinition> Packages => GetSnapshot().Packages.Values.ToImmutableArray();

    public bool TryGetPackage(string packageId, out InstallPackageDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            definition = default!;
            return false;
        }

        var snapshot = GetSnapshot();

        if (snapshot.Packages.TryGetValue(packageId.Trim(), out var found))
        {
            definition = found;
            return true;
        }

        definition = default!;
        return false;
    }

    public InstallBundleDefinition? GetBundle(string bundleId)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
        {
            return null;
        }

        return _snapshot.Value.BundleLookup.TryGetValue(bundleId, out var bundle) ? bundle : null;
    }

    public ImmutableArray<InstallPackageDefinition> GetPackagesForBundle(string bundleId)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
        {
            return ImmutableArray<InstallPackageDefinition>.Empty;
        }

        var snapshot = GetSnapshot();

        if (!snapshot.BundleLookup.TryGetValue(bundleId, out var bundle))
        {
            return ImmutableArray<InstallPackageDefinition>.Empty;
        }

        var results = ImmutableArray.CreateBuilder<InstallPackageDefinition>();

        foreach (var packageId in bundle.PackageIds)
        {
            if (snapshot.Packages.TryGetValue(packageId, out var package))
            {
                results.Add(package);
            }
        }

        return results.ToImmutable();
    }

    public ImmutableArray<InstallPackageDefinition> ResolvePackages(IEnumerable<string> packageIds)
    {
        if (packageIds is null)
        {
            return ImmutableArray<InstallPackageDefinition>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<InstallPackageDefinition>();
        var snapshot = GetSnapshot();

        foreach (var id in packageIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (snapshot.Packages.TryGetValue(id.Trim(), out var definition))
            {
                builder.Add(definition);
            }
        }

        return builder.ToImmutable();
    }

    private Lazy<CatalogSnapshot> CreateSnapshotFactory()
    {
        return new Lazy<CatalogSnapshot>(LoadCatalog, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private CatalogSnapshot GetSnapshot()
    {
        try
        {
            return _snapshot.Value;
        }
        catch
        {
            lock (_snapshotLock)
            {
                _snapshot = CreateSnapshotFactory();
                return _snapshot.Value;
            }
        }
    }

    private static string ResolveCatalogDirectory()
    {
        var relativePath = Path.Combine("data", "catalog");
        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, relativePath);

        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Unable to locate install catalog directory at '{relativePath}'.");
    }

    private CatalogSnapshot LoadCatalog()
    {
        string catalogDirectory;

        try
        {
            catalogDirectory = ResolveCatalogDirectory();
        }
        catch (DirectoryNotFoundException)
        {
            return new CatalogSnapshot(string.Empty, ImmutableArray<InstallBundleDefinition>.Empty, new Dictionary<string, InstallPackageDefinition>(), new Dictionary<string, InstallBundleDefinition>());
        }

        var catalogFiles = Directory.EnumerateFiles(catalogDirectory, "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(catalogDirectory, "*.yaml", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (catalogFiles.Count == 0)
        {
            return new CatalogSnapshot(catalogDirectory, ImmutableArray<InstallBundleDefinition>.Empty, new Dictionary<string, InstallPackageDefinition>(), new Dictionary<string, InstallBundleDefinition>());
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var bundleDocuments = new List<BundleDocument>();
        var packageDocuments = new List<PackageDocument>();

        foreach (var file in catalogFiles)
        {
            string yaml;
            try
            {
                yaml = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(yaml))
            {
                continue;
            }

            CatalogDocument? document;

            try
            {
                document = deserializer.Deserialize<CatalogDocument>(yaml);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse install catalog segment at '{file}'.", ex);
            }

            if (document?.Bundles is not null)
            {
                bundleDocuments.AddRange(document.Bundles);
            }

            if (document?.Packages is not null)
            {
                packageDocuments.AddRange(document.Packages);
            }
        }

        var packageMap = new Dictionary<string, InstallPackageDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packageDocuments)
        {
            if (package is null || string.IsNullOrWhiteSpace(package.Id))
            {
                continue;
            }

            var identifier = package.Id.Trim();

            var tags = package.Tags is null
                ? ImmutableArray<string>.Empty
                : package.Tags
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();

            var buckets = package.Buckets is null
                ? ImmutableArray<string>.Empty
                : package.Buckets
                    .Where(bucket => !string.IsNullOrWhiteSpace(bucket))
                    .Select(bucket => bucket.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();

            var definition = new InstallPackageDefinition(
                identifier,
                package.Name?.Trim() ?? identifier,
                package.Manager?.Trim() ?? string.Empty,
                package.Command?.Trim() ?? string.Empty,
                package.RequiresAdmin,
                package.Summary?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(package.Homepage) ? null : package.Homepage.Trim(),
                tags,
                buckets);

            if (definition.IsValid)
            {
                packageMap[identifier] = definition;
            }
        }

        var bundles = new List<InstallBundleDefinition>();
        var bundleLookup = new Dictionary<string, InstallBundleDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var bundle in bundleDocuments)
        {
            if (bundle is null || string.IsNullOrWhiteSpace(bundle.Id))
            {
                continue;
            }

            var identifier = bundle.Id.Trim();
            var packages = bundle.Packages is null
                ? ImmutableArray<string>.Empty
                : bundle.Packages
                    .Where(id => !string.IsNullOrWhiteSpace(id) && packageMap.ContainsKey(id.Trim()))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();

            var definition = new InstallBundleDefinition(
                identifier,
                bundle.Name?.Trim() ?? identifier,
                bundle.Description?.Trim() ?? string.Empty,
                packages);

            if (definition.IsValid)
            {
                bundles.Add(definition);
                bundleLookup[identifier] = definition;
            }
        }

        bundles.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        return new CatalogSnapshot(catalogDirectory, bundles.ToImmutableArray(), packageMap, bundleLookup);
    }

    private sealed record CatalogSnapshot(
        string SourcePath,
        ImmutableArray<InstallBundleDefinition> Bundles,
        IReadOnlyDictionary<string, InstallPackageDefinition> Packages,
        IReadOnlyDictionary<string, InstallBundleDefinition> BundleLookup);

    private sealed class CatalogDocument
    {
        public List<BundleDocument>? Bundles { get; set; }
        public List<PackageDocument>? Packages { get; set; }
    }

    private sealed class BundleDocument
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Packages { get; set; }
    }

    private sealed class PackageDocument
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Manager { get; set; }
        public string? Command { get; set; }
        public bool RequiresAdmin { get; set; }
        public string? Summary { get; set; }
        public string? Homepage { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? Buckets { get; set; }
    }
}
