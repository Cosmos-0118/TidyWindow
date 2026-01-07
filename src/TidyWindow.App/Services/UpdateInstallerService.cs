using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

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
        var launched = LaunchInstaller(installerPath);

        return new UpdateInstallationResult(installerPath, hashVerified, launched);
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
        CleanupOldInstallers(targetDirectory, keepPath: null, removeEmptyDirectory: false);
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

    private bool LaunchInstaller(string installerPath)
    {
        var prompt = $"The update installer was downloaded to:\n{installerPath}\n\nRun it now?";
        var choice = ShowInstallerPrompt(prompt);
        if (choice != MessageBoxResult.Yes)
        {
            _activityLog.LogInformation("Updates", "Installer download completed but launch was cancelled by the user.");
            TryDeleteInstaller(installerPath);
            CleanupOldInstallers(Path.GetDirectoryName(installerPath) ?? string.Empty, keepPath: null, removeEmptyDirectory: true);
            return false;
        }

        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
            Arguments = string.Empty
        };

        Process.Start(startInfo);
        _activityLog.LogInformation("Updates", "Installer launched with user confirmation. TidyWindow will close so the update can finish.");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                TryDeleteInstaller(installerPath);
                CleanupOldInstallers(Path.GetDirectoryName(installerPath) ?? string.Empty, keepPath: null, removeEmptyDirectory: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        });

        return true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UpdateInstallerService));
        }
    }

    private static MessageBoxResult ShowInstallerPrompt(string prompt)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => ShowInstallerPrompt(prompt));
        }

        var owner = Application.Current?.MainWindow;
        owner?.Activate();
        return owner is null
            ? MessageBox.Show(prompt, "Run TidyWindow installer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            : MessageBox.Show(owner, prompt, "Run TidyWindow installer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
    }

    private static void CleanupOldInstallers(string directory, string? keepPath, bool removeEmptyDirectory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "TidyWindow-Setup-*.exe", SearchOption.TopDirectoryOnly))
        {
            if (!string.IsNullOrWhiteSpace(keepPath) && string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteInstaller(file);
        }

        if (removeEmptyDirectory && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteInstaller(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore failures; best-effort cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore failures; best-effort cleanup.
        }
    }
}