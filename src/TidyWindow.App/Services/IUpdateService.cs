using System;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

public interface IUpdateService : IDisposable
{
    string CurrentVersion { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    string Channel,
    bool IsUpdateAvailable,
    Uri? DownloadUri,
    Uri? ReleaseNotesUri,
    string? Summary,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    long? InstallerSizeBytes,
    string? Sha256);