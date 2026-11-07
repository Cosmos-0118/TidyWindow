using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>
/// Asks each package manager's official release address what the newest stable release is, by reading where that address redirects to. No page is downloaded,
/// no manager is started, and GitHub's rate-limited API is not used. A lookup that fails for any reason says nothing: it never makes a healthy manager look unhealthy.
/// </summary>
public sealed partial class ManagerUpdateCheck : IDisposable
{
    private readonly HttpClient client;
    private readonly Lock gate = new();
    private readonly Dictionary<ManagerKind, (DateTimeOffset At, string Version)> cache = [];

    /// <summary>How long an answer is reused. Overview re-reads the managers after every finished job; the release does not change that often.</summary>
    public TimeSpan CacheFor { get; init; } = TimeSpan.FromMinutes(30);

    /// <param name="handler">Tests replace the network. The default never follows a redirect: the redirect is the answer.</param>
    public ManagerUpdateCheck(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWindow");
    }

    public static Uri Probe(ManagerKind kind) => new(kind switch
    {
        ManagerKind.Winget => "https://github.com/microsoft/winget-cli/releases/latest",
        ManagerKind.Chocolatey => "https://community.chocolatey.org/api/v2/package/chocolatey",
        _ => "https://github.com/ScoopInstaller/Scoop/releases/latest"
    });

    /// <summary>The version a release address points at, or null when the address is not that manager's own plain release (a pre-release, another repository, a page that is not a release).</summary>
    public static string? ParseLatest(ManagerKind kind, string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var match = kind switch
        {
            ManagerKind.Winget => WingetRelease().Match(location),
            ManagerKind.Chocolatey => ChocolateyPackage().Match(location),
            _ => ScoopRelease().Match(location)
        };
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>True when <paramref name="latest"/> is a higher release than <paramref name="installed"/>. A version that is not a number (Scoop on another branch reports a commit) is never called outdated.</summary>
    public static bool IsNewer(string installed, string latest) => Parse(latest) is { } newest && Parse(installed) is { } current && newest > current;

    private static Version? Parse(string text)
    {
        // "v1.29.380", "2.7.5" and "1.29.380.0" are the same kind of thing; a bare number or a commit has no dot and is not a release.
        var match = Number().Match(text.Trim());
        if (!match.Success) return null;
        var parts = match.Groups[1].Value.Split('.');
        var numbers = new int[4];
        for (var i = 0; i < parts.Length && i < 4; i++)
            if (!int.TryParse(parts[i], out numbers[i])) return null;
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    public async Task<string?> LatestAsync(ManagerKind kind, CancellationToken token = default)
    {
        lock (gate) if (cache.TryGetValue(kind, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor) return hit.Version;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Probe(kind));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location) return null;
            var latest = ParseLatest(kind, new Uri(request.RequestUri!, location).AbsoluteUri);
            if (latest is not null) lock (gate) cache[kind] = (DateTimeOffset.UtcNow, latest);
            return latest;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        // Offline, behind a firewall, or too slow: no answer, not a problem.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException or InvalidOperationException) { return null; }
    }

    /// <summary>The status with <see cref="ManagerStatus.AvailableVersion"/> filled in when a newer release exists, and cleared otherwise. Only a working manager with a known version is looked up.</summary>
    public async Task<ManagerStatus> WithAvailableAsync(ManagerStatus status, CancellationToken token = default)
    {
        if (status.Health != ManagerHealth.Ready || status.Version.Length == 0) return status with { AvailableVersion = null };
        var latest = await LatestAsync(status.Definition.Kind, token).ConfigureAwait(false);
        return status with { AvailableVersion = latest is not null && IsNewer(status.Version, latest) ? latest : null };
    }

    public void Dispose() => client.Dispose();

    [GeneratedRegex(@"^https://github\.com/microsoft/winget-cli/releases/tag/v(\d+(?:\.\d+){1,3})$")]
    private static partial Regex WingetRelease();

    [GeneratedRegex(@"^https://github\.com/ScoopInstaller/Scoop/releases/tag/v(\d+(?:\.\d+){1,3})$")]
    private static partial Regex ScoopRelease();

    [GeneratedRegex(@"^https://packages\.chocolatey\.org/chocolatey\.(\d+(?:\.\d+){1,3})\.nupkg$")]
    private static partial Regex ChocolateyPackage();

    [GeneratedRegex(@"^[vV]?(\d+(?:\.\d+)+)")]
    private static partial Regex Number();
}
