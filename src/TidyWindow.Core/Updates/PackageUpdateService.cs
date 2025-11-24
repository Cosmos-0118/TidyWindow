using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Install;

namespace TidyWindow.Core.Updates;

/// <summary>
/// Provides facilities for inspecting installable packages and orchestrating updates via automation scripts.
/// </summary>
public sealed class PackageUpdateService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions _scriptSerializerOptions = new(_jsonOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly Regex WingetIdRegex = new("--id\\s+(?:\"(?<id>[^\"]+)\"|(?<id>[^\\s]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChocoIdRegex = new("choco\\s+(?:install|upgrade)\\s+(?<id>[^\\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScoopIdRegex = new("scoop\\s+install\\s+(?<id>[^\\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly InstallCatalogService _installCatalogService;

    public PackageUpdateService(PowerShellInvoker powerShellInvoker, InstallCatalogService installCatalogService)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _installCatalogService = installCatalogService ?? throw new ArgumentNullException(nameof(installCatalogService));
    }

    /// <summary>
    /// Retrieves the catalog of installable packages with supported managers.
    /// </summary>
    public Task<IReadOnlyList<PackageCatalogEntry>> GetCatalogAsync()
    {
        var catalog = BuildCatalog();
        return Task.FromResult<IReadOnlyList<PackageCatalogEntry>>(catalog);
    }

    /// <summary>
    /// Executes the package update discovery script and returns update states for the catalog.
    /// </summary>
    public async Task<PackageUpdateScanResult> CheckForUpdatesAsync(IEnumerable<string>? packageIds = null, CancellationToken cancellationToken = default)
    {
        var catalog = BuildCatalog();
        var filter = packageIds?
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<PackageCatalogEntry> payloadCatalog;
        if (filter is not null && filter.Count > 0)
        {
            payloadCatalog = catalog
                .Where(entry => filter.Contains(entry.Id))
                .ToList();
        }
        else
        {
            payloadCatalog = catalog;
        }

        if (payloadCatalog.Count == 0)
        {
            return new PackageUpdateScanResult(Array.Empty<PackageUpdateStatus>(), DateTimeOffset.UtcNow);
        }

        var payloadPath = Path.Combine(Path.GetTempPath(), $"tidywindow-package-catalog-{Guid.NewGuid():N}.json");
        try
        {
            var scriptEntries = payloadCatalog
                .Select(entry => new PackageCatalogScriptEntry(
                    entry.Id,
                    entry.DisplayName,
                    entry.Manager,
                    entry.PackageIdentifier,
                    entry.RequiresAdmin,
                    entry.Description,
                    entry.Notes,
                    entry.FallbackLatestVersion))
                .ToList();

            var payloadJson = JsonSerializer.Serialize(scriptEntries, _scriptSerializerOptions);
            await File.WriteAllTextAsync(payloadPath, payloadJson, cancellationToken).ConfigureAwait(false);

            var scriptPath = ResolveScriptPath(Path.Combine("automation", "scripts", "check-package-updates.ps1"));

            var parameters = new Dictionary<string, object?>
            {
                ["CatalogPath"] = payloadPath
            };

            if (filter is not null && filter.Count > 0)
            {
                parameters["PackageIds"] = filter.ToArray();
            }

            var result = await _powerShellInvoker
                .InvokeScriptAsync(scriptPath, parameters, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException("Package update scan failed: " + string.Join(Environment.NewLine, result.Errors));
            }

            var jsonPayload = JsonPayloadExtractor.ExtractLastJsonBlock(result.Output);
            if (string.IsNullOrWhiteSpace(jsonPayload))
            {
                return new PackageUpdateScanResult(Array.Empty<PackageUpdateStatus>(), DateTimeOffset.UtcNow);
            }

            List<PackageUpdateStatusJson> responses;
            try
            {
                responses = JsonSerializer.Deserialize<List<PackageUpdateStatusJson>>(jsonPayload, _jsonOptions)
                            ?? new List<PackageUpdateStatusJson>();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Package update script returned invalid JSON.", ex);
            }

            var lookup = payloadCatalog.ToDictionary(entry => entry.Id, entry => entry, StringComparer.OrdinalIgnoreCase);

            var statuses = responses
                .Select(response => Map(response, lookup))
                .Where(static status => status is not null)
                .Select(static status => status!)
                .ToList();

            return new PackageUpdateScanResult(statuses, DateTimeOffset.UtcNow);
        }
        finally
        {
            try
            {
                File.Delete(payloadPath);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    /// <summary>
    /// Attempts to update the specified package via its package manager.
    /// </summary>
    public async Task<PackageUpdateOperationResult> UpdatePackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new ArgumentException("Package ID must be provided.", nameof(packageId));
        }

        var catalog = BuildCatalog();
        if (!catalog.Any())
        {
            throw new InvalidOperationException("Install catalog is empty.");
        }

        var lookup = catalog.ToDictionary(entry => entry.Id, entry => entry, StringComparer.OrdinalIgnoreCase);
        if (!lookup.TryGetValue(packageId.Trim(), out var catalogEntry))
        {
            throw new InvalidOperationException($"Package '{packageId}' is not present in the install catalog or is not managed by a supported package manager.");
        }

        var scriptPath = ResolveScriptPath(Path.Combine("automation", "scripts", "update-catalog-package.ps1"));

        var parameters = new Dictionary<string, object?>
        {
            ["Manager"] = catalogEntry.Manager,
            ["PackageId"] = catalogEntry.PackageIdentifier
        };

        var result = await _powerShellInvoker
            .InvokeScriptAsync(scriptPath, parameters, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("Package update failed: " + string.Join(Environment.NewLine, result.Errors));
        }

        var jsonPayload = JsonPayloadExtractor.ExtractLastJsonBlock(result.Output);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            throw new InvalidOperationException("Package update script did not return a result payload.");
        }

        PackageUpdateOperationJson? response;
        try
        {
            response = JsonSerializer.Deserialize<PackageUpdateOperationJson>(jsonPayload, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Package update script returned invalid JSON.", ex);
        }

        if (response is null)
        {
            throw new InvalidOperationException("Package update script returned an empty payload.");
        }

        var beforeState = ParseState(response.StatusBefore);
        var afterState = ParseState(response.StatusAfter);
        var installed = NormalizeVersion(response.InstalledVersion);
        var latest = NormalizeVersion(response.LatestVersion);
        var updateAttempted = response.UpdateAttempted ?? false;
        var exitCode = response.ExitCode ?? result.ExitCode;
        var output = response.Output is null
            ? Array.Empty<string>()
            : response.Output.Select(static line => line?.TrimEnd('\r', '\n') ?? string.Empty).ToArray();

        return new PackageUpdateOperationResult(catalogEntry, beforeState, afterState, installed, latest, updateAttempted, exitCode, output);
    }

    private IReadOnlyList<PackageCatalogEntry> BuildCatalog()
    {
        var results = new Dictionary<string, PackageCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<InstallPackageDefinition> packages;
        try
        {
            packages = _installCatalogService.Packages;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load install catalog: {ex.Message}", ex);
        }

        foreach (var definition in packages)
        {
            if (definition is null)
            {
                continue;
            }

            var manager = (definition.Manager ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(manager))
            {
                continue;
            }

            if (!IsSupportedManager(manager))
            {
                continue;
            }

            var packageIdentifier = ExtractPackageIdentifier(definition, manager);
            if (string.IsNullOrWhiteSpace(packageIdentifier))
            {
                continue;
            }

            var description = string.IsNullOrWhiteSpace(definition.Summary)
                ? definition.Name
                : definition.Summary;

            var notes = BuildNotes(definition, manager);

            var entry = new PackageCatalogEntry(
                id: definition.Id,
                displayName: definition.Name,
                manager: NormalizeManager(manager),
                packageIdentifier: packageIdentifier,
                requiresAdmin: definition.RequiresAdmin,
                description: description,
                notes: notes,
                homepage: definition.Homepage,
                fallbackLatestVersion: null);

            results[entry.Id] = entry;
        }

        return results.Values
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSupportedManager(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "winget" or "choco" or "chocolatey" or "scoop";
    }

    private static string NormalizeManager(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "chocolatey" => "choco",
            _ => normalized
        };
    }

    private static string BuildNotes(InstallPackageDefinition definition, string manager)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(definition.Summary))
        {
            parts.Add(definition.Summary.Trim());
        }

        parts.Add($"Managed via {manager.Trim()}.");
        if (definition.RequiresAdmin)
        {
            parts.Add("Requires elevation to update.");
        }

        return string.Join(" ", parts);
    }

    private static string? ExtractPackageIdentifier(InstallPackageDefinition definition, string manager)
    {
        var command = definition.Command ?? string.Empty;
        var normalizedManager = manager.Trim().ToLowerInvariant();

        return normalizedManager switch
        {
            "winget" => ExtractWithRegex(command, WingetIdRegex) ?? definition.Id,
            "choco" => ExtractWithRegex(command, ChocoIdRegex) ?? definition.Id,
            "chocolatey" => ExtractWithRegex(command, ChocoIdRegex) ?? definition.Id,
            "scoop" => ExtractWithRegex(command, ScoopIdRegex) ?? definition.Id,
            _ => definition.Id
        };
    }

    private static string? ExtractWithRegex(string? command, Regex regex)
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
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
    }

    private static PackageUpdateStatus? Map(PackageUpdateStatusJson json, IReadOnlyDictionary<string, PackageCatalogEntry> lookup)
    {
        if (string.IsNullOrWhiteSpace(json.Id))
        {
            return null;
        }

        if (!lookup.TryGetValue(json.Id.Trim(), out var catalogEntry))
        {
            return null;
        }

        var state = ParseState(json.Status);
        var installed = NormalizeVersion(json.InstalledVersion);
        var latest = NormalizeVersion(json.LatestVersion);
        var notes = string.IsNullOrWhiteSpace(json.Notes) ? catalogEntry.Notes : json.Notes!.Trim();

        return new PackageUpdateStatus(catalogEntry, state, installed, latest, notes);
    }

    private static PackageUpdateState ParseState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PackageUpdateState.Unknown;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "uptodate" => PackageUpdateState.UpToDate,
            "updateavailable" => PackageUpdateState.UpdateAvailable,
            "notinstalled" => PackageUpdateState.NotInstalled,
            _ => PackageUpdateState.Unknown
        };
    }

    private static string NormalizeVersion(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Not detected" : value.Trim();
    }

    private static string ResolveScriptPath(string relativePath)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, relativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate automation script at relative path '{relativePath}'.");
    }

    private sealed record PackageCatalogScriptEntry(
        string Id,
        string DisplayName,
        string Manager,
        string PackageId,
        bool RequiresAdmin,
        string Description,
        string Notes,
        string? FallbackLatestVersion);

    private sealed class PackageUpdateStatusJson
    {
        public string? Id { get; set; }
        public string? Status { get; set; }
        public string? InstalledVersion { get; set; }
        public string? LatestVersion { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class PackageUpdateOperationJson
    {
        public string? StatusBefore { get; set; }
        public string? StatusAfter { get; set; }
        public string? InstalledVersion { get; set; }
        public string? LatestVersion { get; set; }
        public bool? UpdateAttempted { get; set; }
        public int? ExitCode { get; set; }
        public List<string>? Output { get; set; }
    }
}

public sealed class PackageCatalogEntry
{
    public PackageCatalogEntry(string id, string displayName, string manager, string packageIdentifier, bool requiresAdmin, string description, string notes, string? homepage, string? fallbackLatestVersion)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(manager))
        {
            throw new ArgumentException("Manager cannot be null or whitespace.", nameof(manager));
        }

        if (string.IsNullOrWhiteSpace(packageIdentifier))
        {
            throw new ArgumentException("Package identifier cannot be null or whitespace.", nameof(packageIdentifier));
        }

        Id = id.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
        Manager = manager.Trim();
        PackageIdentifier = packageIdentifier.Trim();
        RequiresAdmin = requiresAdmin;
        Description = description ?? string.Empty;
        Notes = notes ?? string.Empty;
        Homepage = string.IsNullOrWhiteSpace(homepage) ? null : homepage.Trim();
        FallbackLatestVersion = string.IsNullOrWhiteSpace(fallbackLatestVersion) ? null : fallbackLatestVersion.Trim();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Manager { get; }

    public string PackageIdentifier { get; }

    public bool RequiresAdmin { get; }

    public string Description { get; }

    public string Notes { get; }

    public string? Homepage { get; }

    public string? FallbackLatestVersion { get; }

    public string ManagerDisplayName => Manager switch
    {
        "winget" => "winget",
        "choco" => "Chocolatey",
        "scoop" => "Scoop",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(Manager)
    };
}

public enum PackageUpdateState
{
    Unknown,
    UpToDate,
    UpdateAvailable,
    NotInstalled
}

public sealed class PackageUpdateStatus
{
    public PackageUpdateStatus(PackageCatalogEntry catalogEntry, PackageUpdateState state, string installedVersion, string latestVersion, string notes)
    {
        CatalogEntry = catalogEntry ?? throw new ArgumentNullException(nameof(catalogEntry));
        State = state;
        InstalledVersion = installedVersion ?? "Not detected";
        LatestVersion = latestVersion ?? "Not detected";
        Notes = notes ?? string.Empty;
    }

    public PackageCatalogEntry CatalogEntry { get; }

    public PackageUpdateState State { get; }

    public string InstalledVersion { get; }

    public string LatestVersion { get; }

    public string Notes { get; }

    public bool IsUpdateAvailable => State == PackageUpdateState.UpdateAvailable;

    public bool IsInstalled => State is PackageUpdateState.UpToDate or PackageUpdateState.UpdateAvailable;
}

public sealed class PackageUpdateScanResult
{
    public PackageUpdateScanResult(IReadOnlyList<PackageUpdateStatus> packages, DateTimeOffset generatedAt)
    {
        Packages = packages ?? Array.Empty<PackageUpdateStatus>();
        GeneratedAt = generatedAt;
    }

    public IReadOnlyList<PackageUpdateStatus> Packages { get; }

    public DateTimeOffset GeneratedAt { get; }

    public int UpdateCount => Packages.Count(static package => package.IsUpdateAvailable);

    public int InstalledCount => Packages.Count(static package => package.IsInstalled);
}

public sealed class PackageUpdateOperationResult
{
    public PackageUpdateOperationResult(PackageCatalogEntry catalogEntry, PackageUpdateState beforeState, PackageUpdateState afterState, string installedVersion, string latestVersion, bool updateAttempted, int exitCode, IReadOnlyList<string> output)
    {
        CatalogEntry = catalogEntry ?? throw new ArgumentNullException(nameof(catalogEntry));
        BeforeState = beforeState;
        AfterState = afterState;
        InstalledVersion = installedVersion ?? "Not detected";
        LatestVersion = latestVersion ?? "Not detected";
        UpdateAttempted = updateAttempted;
        ExitCode = exitCode;
        Output = output ?? Array.Empty<string>();
    }

    public PackageCatalogEntry CatalogEntry { get; }

    public PackageUpdateState BeforeState { get; }

    public PackageUpdateState AfterState { get; }

    public string InstalledVersion { get; }

    public string LatestVersion { get; }

    public bool UpdateAttempted { get; }

    public int ExitCode { get; }

    public IReadOnlyList<string> Output { get; }

    public bool WasUpdated => BeforeState == PackageUpdateState.UpdateAvailable && AfterState == PackageUpdateState.UpToDate && ExitCode == 0;

    public bool AlreadyCurrent => BeforeState != PackageUpdateState.UpdateAvailable && AfterState == PackageUpdateState.UpToDate;

    public bool Failed => ExitCode != 0 || (BeforeState == PackageUpdateState.UpdateAvailable && AfterState == PackageUpdateState.UpdateAvailable);
}
