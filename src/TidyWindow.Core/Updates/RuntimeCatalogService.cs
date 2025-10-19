using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Updates;

/// <summary>
/// Provides metadata about monitored runtimes and coordinates update checks via automation scripts.
/// </summary>
public sealed class RuntimeCatalogService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PowerShellInvoker _powerShellInvoker;

    private static readonly ReadOnlyCollection<RuntimeCatalogEntry> _catalog = new(new[]
    {
        new RuntimeCatalogEntry(
            id: "dotnet-desktop",
            displayName: ".NET Desktop Runtime",
            vendor: "Microsoft",
            description: "Required for running modern WPF and WinUI applications.",
            downloadUrl: "https://dotnet.microsoft.com/en-us/download/dotnet"),
        new RuntimeCatalogEntry(
            id: "powershell",
            displayName: "PowerShell 7",
            vendor: "Microsoft",
            description: "Latest cross-platform automation shell with updated cmdlets and security fixes.",
            downloadUrl: "https://aka.ms/powershell-release"),
        new RuntimeCatalogEntry(
            id: "node-lts",
            displayName: "Node.js LTS",
            vendor: "OpenJS Foundation",
            description: "JavaScript tooling runtime used by many CLIs and build pipelines.",
            downloadUrl: "https://nodejs.org/en/download")
    });

    public RuntimeCatalogService(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker;
    }

    /// <summary>
    /// Returns the static runtime catalog tracked by the application.
    /// </summary>
    public Task<IReadOnlyList<RuntimeCatalogEntry>> GetCatalogAsync()
    {
        return Task.FromResult<IReadOnlyList<RuntimeCatalogEntry>>(_catalog);
    }

    /// <summary>
    /// Executes the runtime update script and merges the results with catalog metadata.
    /// </summary>
    public async Task<RuntimeUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var scriptPath = ResolveScriptPath(Path.Combine("automation", "scripts", "check-runtime-updates.ps1"));

        var parameters = new Dictionary<string, object?>
        {
            ["RuntimeIds"] = _catalog.Select(static entry => entry.Id).ToArray()
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
                .Select(Map)
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

    private RuntimeUpdateStatus? Map(RuntimeUpdateStatusJson json)
    {
        if (string.IsNullOrWhiteSpace(json.Id))
        {
            return null;
        }

        var catalogEntry = _catalog.FirstOrDefault(entry =>
            string.Equals(entry.Id, json.Id, StringComparison.OrdinalIgnoreCase));

        if (catalogEntry is null)
        {
            return null;
        }

        var state = ParseState(json.Status);
        var installed = NormalizeVersion(json.InstalledVersion);
        var latest = NormalizeVersion(json.LatestVersion);
        var downloadUrl = string.IsNullOrWhiteSpace(json.DownloadUrl)
            ? catalogEntry.DownloadUrl
            : json.DownloadUrl!;
        var notes = json.Notes ?? string.Empty;

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
    public RuntimeCatalogEntry(string id, string displayName, string vendor, string description, string downloadUrl)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(id)) : id;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
        Vendor = vendor ?? string.Empty;
        Description = description ?? string.Empty;
        DownloadUrl = string.IsNullOrWhiteSpace(downloadUrl) ? "https://" : downloadUrl;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Vendor { get; }

    public string Description { get; }

    public string DownloadUrl { get; }
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
