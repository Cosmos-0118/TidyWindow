using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

public sealed class UpdateInstallerService : IUpdateInstallerService
{
    private readonly HttpClient _httpClient;
    private readonly ActivityLogService _activityLog;
    private bool _disposed;

    public UpdateInstallerService(ActivityLogService activityLog)
        : this(new HttpClient(), activityLog)
    {
    }

    internal UpdateInstallerService(HttpClient httpClient, ActivityLogService activityLog)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
    }

    public async Task<UpdateInstallationResult> DownloadAndInstallAsync(
        UpdateCheckResult update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        if (update.DownloadUri is null)
        {
            throw new InvalidOperationException("Update manifest is missing a download link.");
        }

        var installerPath = await DownloadInstallerAsync(update, progress, cancellationToken).ConfigureAwait(false);
        var hashVerified = await VerifyInstallerAsync(installerPath, update.Sha256, cancellationToken).ConfigureAwait(false);
        LaunchInstaller(installerPath);

        return new UpdateInstallationResult(installerPath, hashVerified);
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

    private async Task<string> DownloadInstallerAsync(UpdateCheckResult update, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var targetDirectory = Path.Combine(Path.GetTempPath(), "TidyWindow", "Updates");
        Directory.CreateDirectory(targetDirectory);
        var fileName = BuildInstallerFileName(update);
        var filePath = Path.Combine(targetDirectory, fileName);

        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(filePath);

        var buffer = new byte[81920];
        long totalRead = 0;
        var contentLength = response.Content.Headers.ContentLength;

        while (true)
        {
            var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
            progress?.Report(new UpdateDownloadProgress(totalRead, contentLength));
        }

        _activityLog.LogInformation("Updates", $"Installer downloaded to {filePath} ({FormatBytes(totalRead)}).");
        return filePath;
    }

    private static string BuildInstallerFileName(UpdateCheckResult update)
    {
        var version = string.IsNullOrWhiteSpace(update.LatestVersion) ? "latest" : update.LatestVersion.Replace(' ', '_');
        return $"TidyWindow-Setup-{version}.exe";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] sizes = { "B", "KB", "MB", "GB" };
        var order = (int)Math.Min(sizes.Length - 1, Math.Log(bytes, 1024));
        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", bytes / Math.Pow(1024, order), sizes[order]);
    }

    private static async Task<bool> VerifyInstallerAsync(string installerPath, string? expectedHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var normalizedExpected = expectedHash.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

        await using var stream = File.OpenRead(installerPath);
        using var sha = SHA256.Create();
        var computed = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();

        if (!string.Equals(computedHex, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Downloaded installer failed the integrity check.");
        }

        return true;
    }

    private void LaunchInstaller(string installerPath)
    {
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
        };

        Process.Start(startInfo);
        _activityLog.LogInformation("Updates", "Installer launched. TidyWindow will close so the update can finish.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UpdateInstallerService));
        }
    }
}