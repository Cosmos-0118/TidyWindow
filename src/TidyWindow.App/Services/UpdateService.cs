using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

public sealed class UpdateService : IUpdateService
{
    private const string DefaultManifestUrl = "https://raw.githubusercontent.com/Cosmos-0118/TidyWindow/main/data/catalog/latest-release.json";
    private const string FallbackManifestRelativePath = "data/catalog/latest-release.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _manifestEndpoint;
    private readonly string _currentVersion;
    private readonly string? _fallbackManifestPath;
    private bool _disposed;

    public UpdateService()
        : this(CreateDefaultClient(), DefaultManifestUrl)
    {
    }

    internal UpdateService(HttpClient httpClient, string manifestEndpoint, string? fallbackManifestPath = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _manifestEndpoint = string.IsNullOrWhiteSpace(manifestEndpoint)
            ? throw new ArgumentException("Manifest endpoint must be provided.", nameof(manifestEndpoint))
            : manifestEndpoint;
        _currentVersion = ResolveCurrentVersion();
        _fallbackManifestPath = string.IsNullOrWhiteSpace(fallbackManifestPath)
            ? Path.Combine(AppContext.BaseDirectory, FallbackManifestRelativePath.Replace('/', Path.DirectorySeparatorChar))
            : fallbackManifestPath;
    }

    public string CurrentVersion => _currentVersion;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        UpdateManifest? manifest = null;

        try
        {
            manifest = await LoadManifestFromEndpointAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!TryLoadFallbackManifest(out manifest))
            {
                throw;
            }
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("Update manifest is missing the version field.");
        }

        var latestVersion = NormalizeVersion(manifest.Version);
        var currentVersion = NormalizeVersion(CurrentVersion);
        var updateAvailable = IsNewerVersion(latestVersion, currentVersion);

        return new UpdateCheckResult(
            currentVersion,
            latestVersion,
            string.IsNullOrWhiteSpace(manifest.Channel) ? "stable" : manifest.Channel.Trim(),
            updateAvailable,
            TryCreateUri(manifest.DownloadUrl),
            TryCreateUri(manifest.ReleaseNotesUrl),
            string.IsNullOrWhiteSpace(manifest.Summary) ? null : manifest.Summary.Trim(),
            DateTimeOffset.UtcNow,
            manifest.PublishedAt,
            manifest.InstallerSizeBytes,
            string.IsNullOrWhiteSpace(manifest.Sha256) ? null : manifest.Sha256.Trim());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TidyWindow", ResolveCurrentVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string ResolveCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion.Trim();
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion?.ToString() ?? "0.0.0";
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    private static bool IsNewerVersion(string candidate, string baseline)
    {
        if (!Version.TryParse(candidate, out var candidateVersion))
        {
            return false;
        }

        if (!Version.TryParse(baseline, out var baselineVersion))
        {
            return true;
        }

        return candidateVersion > baselineVersion;
    }

    private static Uri? TryCreateUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ? uri : null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UpdateService));
        }
    }

    private async Task<UpdateManifest?> LoadManifestFromEndpointAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _manifestEndpoint);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var reason = response.ReasonPhrase ?? "Unknown";
            throw new HttpRequestException($"Manifest endpoint returned {(int)response.StatusCode} ({reason}).");
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<UpdateManifest>(contentStream, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private bool TryLoadFallbackManifest(out UpdateManifest? manifest)
    {
        manifest = null;
        var path = _fallbackManifestPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            manifest = JsonSerializer.Deserialize<UpdateManifest>(stream, SerializerOptions);
            return manifest is not null;
        }
        catch
        {
            manifest = null;
            return false;
        }
    }

    private sealed record UpdateManifest
    {
        public string Version { get; init; } = string.Empty;

        public string? Channel { get; init; }

        public string? Summary { get; init; }

        public string? DownloadUrl { get; init; }

        public string? ReleaseNotesUrl { get; init; }

        public DateTimeOffset? PublishedAt { get; init; }

        public long? InstallerSizeBytes { get; init; }

        public string? Sha256 { get; init; }
    }
}