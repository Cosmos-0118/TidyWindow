using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TidyWindow.Core.Install;

public sealed class BundlePresetService
{
    private readonly InstallCatalogService _catalogService;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public BundlePresetService(InstallCatalogService catalogService)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitEmptyCollections)
            .Build();
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public async Task SavePresetAsync(string path, BundlePreset preset, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be provided.", nameof(path));
        }

        if (preset is null)
        {
            throw new ArgumentNullException(nameof(preset));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new PresetDocument
        {
            Name = preset.Name,
            Description = preset.Description,
            Packages = preset.PackageIds.ToArray()
        };

        var yaml = _serializer.Serialize(document);
        var encoded = Encoding.UTF8.GetBytes(yaml);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await stream.WriteAsync(encoded.AsMemory(0, encoded.Length), cancellationToken).ConfigureAwait(false);
    }

    public async Task<BundlePreset> LoadPresetAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be provided.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Preset file not found.", path);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Preset file is empty.");
        }

        PresetDocument? document;
        try
        {
            document = _deserializer.Deserialize<PresetDocument>(content);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to parse preset file.", ex);
        }

        document ??= new PresetDocument();

        var packageIds = document.Packages is null
            ? ImmutableArray<string>.Empty
            : document.Packages
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

        return new BundlePreset(
            document.Name?.Trim() ?? "Imported preset",
            document.Description?.Trim() ?? string.Empty,
            packageIds);
    }

    public BundlePresetResolution ResolvePackages(BundlePreset preset)
    {
        if (preset is null)
        {
            throw new ArgumentNullException(nameof(preset));
        }

        var found = new List<InstallPackageDefinition>();
        var missing = new List<string>();

        foreach (var id in preset.PackageIds)
        {
            if (_catalogService.TryGetPackage(id, out var package))
            {
                found.Add(package);
            }
            else
            {
                missing.Add(id);
            }
        }

        return new BundlePresetResolution(found.ToImmutableArray(), missing.ToImmutableArray());
    }

    private sealed class PresetDocument
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public IReadOnlyList<string>? Packages { get; set; }
    }
}

public sealed record BundlePreset(string Name, string Description, ImmutableArray<string> PackageIds)
{
    public bool HasPackages => PackageIds.Length > 0;
}

public sealed record BundlePresetResolution(ImmutableArray<InstallPackageDefinition> Packages, ImmutableArray<string> Missing);
