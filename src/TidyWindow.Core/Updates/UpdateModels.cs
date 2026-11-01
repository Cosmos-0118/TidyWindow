namespace TidyWindow.Next.Core;

public sealed record AppReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

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
