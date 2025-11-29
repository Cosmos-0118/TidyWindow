using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Install;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Executes the package inventory automation script and merges the results with catalog metadata.
/// </summary>
public sealed class PackageInventoryService
{
    private const string ScriptRelativePath = "automation/scripts/get-package-inventory.ps1";
    private const string ScriptOverrideEnvironmentVariable = "TIDYWINDOW_PACKAGE_INVENTORY_SCRIPT";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Regex WingetIdRegex = new("--id\\s+(?:\"(?<id>[^\"]+)\"|(?<id>[^\\s]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChocoIdRegex = new("choco\\s+(?:install|upgrade)\\s+(?<id>[^\\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScoopIdRegex = new("scoop\\s+install\\s+(?<id>[^\\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly InstallCatalogService _installCatalogService;

    public PackageInventoryService(PowerShellInvoker powerShellInvoker, InstallCatalogService installCatalogService)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _installCatalogService = installCatalogService ?? throw new ArgumentNullException(nameof(installCatalogService));
    }

    /// <summary>
    /// Executes the automation script to collect installed package data and enriches it with catalog metadata.
    /// </summary>
    public async Task<PackageInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        var scriptPath = ResolveScriptPath();

        var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("Package inventory script failed: " + string.Join(Environment.NewLine, result.Errors));
        }

        var jsonPayload = JsonPayloadExtractor.ExtractLastJsonBlock(result.Output);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            throw new InvalidOperationException("Package inventory script returned no JSON payload.");
        }

        PackageInventoryScriptPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<PackageInventoryScriptPayload>(jsonPayload, _jsonOptions)
                      ?? new PackageInventoryScriptPayload();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Package inventory script returned invalid JSON.", ex);
        }

        var generatedAt = TryParseTimestamp(payload.GeneratedAt) ?? DateTimeOffset.UtcNow;
        var catalogLookup = BuildCatalogLookup();

        var packages = payload.Packages is null
            ? ImmutableArray<PackageInventoryItem>.Empty
            : payload.Packages
                .Select(item => Map(item, catalogLookup))
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderBy(item => item.Manager, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

        var warnings = payload.Warnings is null
            ? ImmutableArray<string>.Empty
            : payload.Warnings
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Select(warning => warning.Trim())
                .ToImmutableArray();

        return new PackageInventorySnapshot(packages, warnings, generatedAt);
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string ResolveScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(ScriptOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, ScriptRelativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, ScriptRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate automation script at '{ScriptRelativePath}'.");
    }

    private IReadOnlyDictionary<string, PackageCatalogMetadata> BuildCatalogLookup()
    {
        var lookup = new Dictionary<string, PackageCatalogMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in _installCatalogService.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.Manager))
            {
                continue;
            }

            var manager = package.Manager.Trim();
            var identifier = ExtractPackageIdentifier(package);
            var metadata = new PackageCatalogMetadata(
                package.Id,
                package.Name,
                package.Summary,
                package.Homepage,
                package.RequiresAdmin,
                package.Tags,
                package.Command);

            void AddKey(string keyIdentifier)
            {
                if (string.IsNullOrWhiteSpace(keyIdentifier))
                {
                    return;
                }

                var key = BuildKey(manager, keyIdentifier);
                lookup[key] = metadata;
            }

            AddKey(identifier ?? string.Empty);
            AddKey(package.Id);
        }

        return lookup;
    }

    private static PackageInventoryItem? Map(PackageInventoryScriptItem item, IReadOnlyDictionary<string, PackageCatalogMetadata> catalogLookup)
    {
        if (item is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(item.Manager) || string.IsNullOrWhiteSpace(item.Id))
        {
            return null;
        }

        var manager = item.Manager.Trim();
        var identifier = item.Id.Trim();
        var source = string.IsNullOrWhiteSpace(item.Source) ? string.Empty : item.Source.Trim();
        var name = string.IsNullOrWhiteSpace(item.Name) ? identifier : item.Name.Trim();
        var installed = NormalizeVersion(item.InstalledVersion) ?? "Unknown";
        var available = NormalizeVersion(item.AvailableVersion);

        var key = BuildKey(manager, identifier);
        catalogLookup.TryGetValue(key, out var metadata);

        if (metadata is null)
        {
            // Attempt fallback using normalized identifier if commands omitted bucket/source suffixes.
            var fallbackIdentifier = identifier.Contains('/') || identifier.Contains('.')
                ? identifier
                : identifier + string.Empty; // no-op but clarifies intent
            catalogLookup.TryGetValue(BuildKey(manager, fallbackIdentifier), out metadata);
        }

        var hasUpdate = available is not null && !string.Equals(installed, available, StringComparison.OrdinalIgnoreCase);

        return new PackageInventoryItem(
            manager,
            identifier,
            name,
            installed,
            available,
            source,
            hasUpdate,
            metadata);
    }

    private static string BuildKey(string manager, string identifier)
    {
        return manager.Trim().ToLowerInvariant() + "|" + identifier.Trim();
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return VersionStringHelper.Normalize(value);
    }

    private static string? ExtractPackageIdentifier(InstallPackageDefinition definition)
    {
        if (definition is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(definition.Manager))
        {
            return null;
        }

        var manager = definition.Manager.Trim().ToLowerInvariant();
        var command = definition.Command ?? string.Empty;

        return manager switch
        {
            "winget" => ExtractWithRegex(command, WingetIdRegex) ?? definition.Id,
            "choco" => ExtractWithRegex(command, ChocoIdRegex) ?? definition.Id,
            "chocolatey" => ExtractWithRegex(command, ChocoIdRegex) ?? definition.Id,
            "scoop" => ExtractWithRegex(command, ScoopIdRegex) ?? definition.Id,
            _ => definition.Id
        };
    }

    private static string? ExtractWithRegex(string command, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var match = regex.Match(command);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["id"].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim('"', ' ');
    }

    private sealed class PackageInventoryScriptPayload
    {
        public string? GeneratedAt { get; set; }

        public List<string>? Warnings { get; set; }

        public List<PackageInventoryScriptItem>? Packages { get; set; }
    }

    private sealed class PackageInventoryScriptItem
    {
        public string? Manager { get; set; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? InstalledVersion { get; set; }

        public string? AvailableVersion { get; set; }

        public string? Source { get; set; }
    }
}

public sealed record PackageInventorySnapshot(
    ImmutableArray<PackageInventoryItem> Packages,
    ImmutableArray<string> Warnings,
    DateTimeOffset GeneratedAt);

public sealed record PackageInventoryItem(
    string Manager,
    string PackageIdentifier,
    string Name,
    string InstalledVersion,
    string? AvailableVersion,
    string Source,
    bool IsUpdateAvailable,
    PackageCatalogMetadata? Catalog);

public sealed record PackageCatalogMetadata(
    string InstallPackageId,
    string DisplayName,
    string Summary,
    string? Homepage,
    bool RequiresAdmin,
    ImmutableArray<string> Tags,
    string Command);
