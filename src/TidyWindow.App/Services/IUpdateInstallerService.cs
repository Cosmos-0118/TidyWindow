using System;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

public interface IUpdateInstallerService : IDisposable
{
    Task<UpdateInstallationResult> DownloadAndInstallAsync(
        UpdateCheckResult update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes);

public sealed record UpdateInstallationResult(string InstallerPath, bool HashVerified, bool Launched);