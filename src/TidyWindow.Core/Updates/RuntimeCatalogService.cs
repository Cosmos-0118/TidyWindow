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
/// Provides metadata about monitored runtimes and coordinates update checks via automation scripts.
/// </summary>
public sealed class RuntimeCatalogService
{
    public static class RuntimeDetectorKeys
    {
        public const string DotNetDesktop = "dotnet-desktop";
        public const string PowerShell = "powershell";
        public const string NodeLts = "node-lts";
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly InstallCatalogService _installCatalogService;

    private static readonly Regex WingetIdRegex = new("--id\\s+(?:\"(?<id>[^\"]+)\"|(?<id>[^\\s]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChocoIdRegex = new("choco\\s+(?:install|upgrade)\\s+(?<id>[^\\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScoopIdRegex = new("scoop\\s+install\\s+(?<id>[^\\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly ReadOnlyCollection<RuntimeCatalogEntry> _baseCatalog = new(new[]
    {
        new RuntimeCatalogEntry(
            id: "dotnet-desktop",
            displayName: ".NET Desktop Runtime",
            vendor: "Microsoft",
            description: "Required for running modern WPF and WinUI applications.",
            downloadUrl: "https://dotnet.microsoft.com/en-us/download/dotnet",
            notes: "Installs Microsoft.WindowsDesktop.App for WPF/WinForms applications.",
            detectorKey: RuntimeDetectorKeys.DotNetDesktop,
            manager: "winget",
            packageIdentifier: "Microsoft.DotNet.DesktopRuntime.8",
            fallbackLatestVersion: "8.0.10",
            installPackageId: "dotnet-runtime-8"),
        new RuntimeCatalogEntry(
            id: "powershell",
            displayName: "PowerShell 7",
            vendor: "Microsoft",
            description: "Latest cross-platform automation shell with updated cmdlets and security fixes.",
            downloadUrl: "https://aka.ms/powershell-release",
            notes: "Provides the latest cross-platform automation shell.",
            detectorKey: RuntimeDetectorKeys.PowerShell,
            manager: "winget",
            packageIdentifier: "Microsoft.PowerShell",
            fallbackLatestVersion: "7.5.3",
            installPackageId: "powershell7"),
        new RuntimeCatalogEntry(
            id: "node-lts",
            displayName: "Node.js LTS",
            vendor: "OpenJS Foundation",
            description: "JavaScript tooling runtime used by many CLIs and build pipelines.",
            downloadUrl: "https://nodejs.org/en/download",
            notes: "Used by JavaScript tooling and popular CLI experiences.",
            detectorKey: RuntimeDetectorKeys.NodeLts,
            manager: "winget",
            packageIdentifier: "OpenJS.NodeJS.LTS",
            fallbackLatestVersion: "22.20.0",
            installPackageId: "nodejs-lts")
    });

    private static readonly JsonSerializerOptions _scriptSerializerOptions = new(_jsonOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public RuntimeCatalogService(PowerShellInvoker powerShellInvoker, InstallCatalogService installCatalogService)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _installCatalogService = installCatalogService ?? throw new ArgumentNullException(nameof(installCatalogService));
    }

    /// <summary>
    /// Returns the static runtime catalog tracked by the application.
    /// </summary>
    public Task<IReadOnlyList<RuntimeCatalogEntry>> GetCatalogAsync()
    {
        var catalog = BuildCatalog();
        return Task.FromResult<IReadOnlyList<RuntimeCatalogEntry>>(catalog);
    }

    private IReadOnlyList<RuntimeCatalogEntry> BuildCatalog()
    {
        var lookup = new Dictionary<string, RuntimeCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _baseCatalog)
        {
            lookup[entry.Id] = entry;
        }

        foreach (var package in _installCatalogService.Packages)
        {
            if (!package.Tags.Any(static tag => string.Equals(tag, "runtime", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var runtimeEntry = CreateRuntimeEntry(package);
            if (runtimeEntry is not null)
            {
                lookup[runtimeEntry.Id] = runtimeEntry;
            }
        }

        return lookup.Values
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static RuntimeCatalogEntry? CreateRuntimeEntry(InstallPackageDefinition definition)
    {
        if (definition is null)
        {
            return null;
        }

        var manager = (definition.Manager ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(manager))
        {
            return null;
        }

        var packageIdentifier = ExtractPackageIdentifier(definition);
        if (string.IsNullOrWhiteSpace(packageIdentifier))
        {
            return null;
        }

        var description = string.IsNullOrWhiteSpace(definition.Summary)
            ? definition.Name
            : definition.Summary;

        var notesParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(definition.Summary))
        {
            notesParts.Add(definition.Summary.Trim());
        }

        notesParts.Add($"Managed via {manager}.");

        return new RuntimeCatalogEntry(
            id: definition.Id,
            displayName: definition.Name,
            vendor: ResolveVendor(manager),
            description: description,
            downloadUrl: definition.Homepage ?? "https://",
            notes: string.Join(" ", notesParts),
            detectorKey: $"manager:{manager}",
            manager: manager,
            packageIdentifier: packageIdentifier,
            fallbackLatestVersion: null,
            installPackageId: definition.Id);
    }

    private static string ResolveVendor(string manager)
    {
        var normalized = (manager ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "winget" => "Microsoft",
            "choco" => "Chocolatey",
            "chocolatey" => "Chocolatey",
            "scoop" => "Scoop",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized)
        };
    }

    private static string? ExtractPackageIdentifier(InstallPackageDefinition definition)
    {
        var manager = (definition.Manager ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(manager))
        {
            return null;
        }

        var command = definition.Command ?? string.Empty;

        return manager.ToLowerInvariant() switch
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

    /// <summary>
    /// Executes the runtime update script and merges the results with catalog metadata.
    /// </summary>
    public async Task<RuntimeUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var catalog = BuildCatalog();
        var scriptPath = ResolveScriptPath(Path.Combine("automation", "scripts", "check-runtime-updates.ps1"));

        var payloadPath = Path.Combine(Path.GetTempPath(), $"tidywindow-runtime-catalog-{Guid.NewGuid():N}.json");

        var scriptPayload = catalog
            .Select(entry => new RuntimeCatalogScriptEntry(
                entry.Id,
                entry.DisplayName,
                entry.DownloadUrl,
                entry.Notes,
                entry.DetectorKey,
                entry.Manager,
                entry.PackageIdentifier,
                entry.FallbackLatestVersion))
            .ToList();

        var payloadJson = JsonSerializer.Serialize(scriptPayload, _scriptSerializerOptions);
        await File.WriteAllTextAsync(payloadPath, payloadJson, cancellationToken).ConfigureAwait(false);

        var catalogLookup = catalog.ToDictionary(entry => entry.Id, entry => entry, StringComparer.OrdinalIgnoreCase);

        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["RuntimeIds"] = catalog.Select(static entry => entry.Id).ToArray(),
                ["CatalogPath"] = payloadPath
            };

            var result = await _powerShellInvoker
                .InvokeScriptAsync(scriptPath, parameters, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    "Runtime update check failed: " + string.Join(Environment.NewLine, result.Errors));
            }

            var jsonPayload = ExtractJsonPayload(result.Output);
            if (string.IsNullOrWhiteSpace(jsonPayload))
            {
                return new RuntimeUpdateCheckResult(Array.Empty<RuntimeUpdateStatus>(), DateTimeOffset.UtcNow);
            }

            try
            {
                var responses = JsonSerializer.Deserialize<List<RuntimeUpdateStatusJson>>(jsonPayload, _jsonOptions)
                                ?? new List<RuntimeUpdateStatusJson>();

                var statuses = responses
                    .Select(response => Map(response, catalogLookup))
                    .Where(static status => status is not null)
                    .Select(static status => status!)
                    .ToList();

                return new RuntimeUpdateCheckResult(statuses, DateTimeOffset.UtcNow);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Runtime update script returned invalid JSON.", ex);
            }
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

    private RuntimeUpdateStatus? Map(RuntimeUpdateStatusJson json, IReadOnlyDictionary<string, RuntimeCatalogEntry> catalogLookup)
    {
        if (string.IsNullOrWhiteSpace(json.Id))
        {
            return null;
        }

        if (!catalogLookup.TryGetValue(json.Id.Trim(), out var catalogEntry))
        {
            return null;
        }

        var state = ParseState(json.Status);
        var installed = NormalizeVersion(json.InstalledVersion);
        var latest = NormalizeVersion(json.LatestVersion);
        var downloadUrl = string.IsNullOrWhiteSpace(json.DownloadUrl)
            ? catalogEntry.DownloadUrl
            : json.DownloadUrl!;
        var notes = string.IsNullOrWhiteSpace(json.Notes)
            ? catalogEntry.Notes
            : json.Notes!.Trim();

        return new RuntimeUpdateStatus(catalogEntry, state, installed, latest, downloadUrl, notes);
    }

    private static RuntimeUpdateState ParseState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RuntimeUpdateState.Unknown;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "uptodate" => RuntimeUpdateState.UpToDate,
            "updateavailable" => RuntimeUpdateState.UpdateAvailable,
            "notinstalled" => RuntimeUpdateState.NotInstalled,
            _ => RuntimeUpdateState.Unknown
        };
    }

    private static string NormalizeVersion(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Not detected" : value.Trim();
    }

    private static string? ExtractJsonPayload(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            var trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            trimmed = trimmed.TrimStart('\uFEFF');
            if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    private sealed record RuntimeCatalogScriptEntry(
        string Id,
        string DisplayName,
        string DownloadUrl,
        string Notes,
        string Detector,
        string Manager,
        string? PackageId,
        string? FallbackLatestVersion);

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

    private sealed class RuntimeUpdateStatusJson
    {
        public string? Id { get; set; }
        public string? Status { get; set; }
        public string? InstalledVersion { get; set; }
        public string? LatestVersion { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Notes { get; set; }
    }
}

public sealed class RuntimeCatalogEntry
{
    public RuntimeCatalogEntry(
        string id,
        string displayName,
        string vendor,
        string description,
        string downloadUrl,
        string notes,
        string detectorKey,
        string manager,
        string? packageIdentifier = null,
        string? fallbackLatestVersion = null,
        string? installPackageId = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(detectorKey))
        {
            throw new ArgumentException("Detector key must be provided.", nameof(detectorKey));
        }

        if (string.IsNullOrWhiteSpace(manager))
        {
            throw new ArgumentException("Manager must be provided.", nameof(manager));
        }

        Id = id;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
        Vendor = vendor ?? string.Empty;
        Description = description ?? string.Empty;
        DownloadUrl = string.IsNullOrWhiteSpace(downloadUrl) ? "https://" : downloadUrl;
        Notes = notes ?? string.Empty;
        DetectorKey = detectorKey;
        Manager = manager.Trim();
        PackageIdentifier = string.IsNullOrWhiteSpace(packageIdentifier) ? null : packageIdentifier.Trim();
        FallbackLatestVersion = string.IsNullOrWhiteSpace(fallbackLatestVersion) ? null : fallbackLatestVersion;
        InstallPackageId = string.IsNullOrWhiteSpace(installPackageId) ? null : installPackageId;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Vendor { get; }

    public string Description { get; }

    public string DownloadUrl { get; }

    public string Notes { get; }

    public string DetectorKey { get; }

    public string Manager { get; }

    public string? PackageIdentifier { get; }

    public string? FallbackLatestVersion { get; }

    public string? InstallPackageId { get; }

    public bool HasInstaller => !string.IsNullOrWhiteSpace(InstallPackageId);
}

public enum RuntimeUpdateState
{
    Unknown,
    UpToDate,
    UpdateAvailable,
    NotInstalled
}

public sealed class RuntimeUpdateStatus
{
    public RuntimeUpdateStatus(RuntimeCatalogEntry catalogEntry, RuntimeUpdateState state, string installedVersion, string latestVersion, string downloadUrl, string notes)
    {
        CatalogEntry = catalogEntry ?? throw new ArgumentNullException(nameof(catalogEntry));
        State = state;
        InstalledVersion = installedVersion ?? "Not detected";
        LatestVersion = latestVersion ?? "Not detected";
        DownloadUrl = downloadUrl ?? catalogEntry.DownloadUrl;
        Notes = notes ?? string.Empty;
    }

    public RuntimeCatalogEntry CatalogEntry { get; }

    public RuntimeUpdateState State { get; }

    public string InstalledVersion { get; }

    public string LatestVersion { get; }

    public string DownloadUrl { get; }

    public string Notes { get; }

    public bool IsUpdateAvailable => State == RuntimeUpdateState.UpdateAvailable;

    public bool IsMissing => State == RuntimeUpdateState.NotInstalled;

    public bool HasInstaller => CatalogEntry.HasInstaller;
}

public sealed class RuntimeUpdateCheckResult
{
    public RuntimeUpdateCheckResult(IReadOnlyList<RuntimeUpdateStatus> runtimes, DateTimeOffset generatedAt)
    {
        Runtimes = runtimes ?? Array.Empty<RuntimeUpdateStatus>();
        GeneratedAt = generatedAt;
    }

    public IReadOnlyList<RuntimeUpdateStatus> Runtimes { get; }

    public DateTimeOffset GeneratedAt { get; }

    public int UpdateCount => Runtimes.Count(static runtime => runtime.IsUpdateAvailable);

    public int MissingCount => Runtimes.Count(static runtime => runtime.IsMissing);
}
