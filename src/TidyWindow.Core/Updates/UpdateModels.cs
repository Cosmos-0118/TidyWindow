namespace TidyWindow.Next.Core;

/// <param name="Sha256">The SHA-256 GitHub lists for the file, as lowercase hex, or null when the release does not list one. A download that does not match it is refused.</param>
public sealed record AppReleaseAsset(string Name, string DownloadUrl, long SizeBytes, string? Sha256 = null);

public sealed record AppRelease(
    string TagName,
    Version Version,
    string Title,
    string Body,
    string CleanNotes,
    DateTimeOffset PublishedAt,
    string HtmlUrl,
    AppReleaseAsset? InstallerAsset,
    AppReleaseAsset? PortableAsset);

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    bool IsInstalledBuild,
    AppRelease? LatestRelease,
    IReadOnlyList<AppRelease> RecentReleases,
    bool UpdateAvailable);

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes, int? Percent);
