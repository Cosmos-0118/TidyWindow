using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace TidyWindow.App.Services;

public enum BrowserCleanupResultStatus
{
    Success,
    Skipped,
    Failed
}

public sealed record BrowserCleanupResult(BrowserCleanupResultStatus Status, string Message, Exception? Exception = null)
{
    public bool IsSuccess => Status == BrowserCleanupResultStatus.Success;
}

public enum BrowserKind
{
    Edge,
    Chrome
}

public readonly record struct BrowserProfile(BrowserKind Kind, string ProfileDirectory);

public interface IBrowserCleanupService : IDisposable
{
    Task<BrowserCleanupResult> ClearHistoryAsync(BrowserProfile profile, IReadOnlyList<string> targetPaths, CancellationToken cancellationToken);
}

public sealed class BrowserCleanupService : IBrowserCleanupService
{
    private readonly Dispatcher _dispatcher;
    private readonly object _hostLock = new();
    private HwndSource? _hostSource;
    private bool _disposed;

    public BrowserCleanupService()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public Task<BrowserCleanupResult> ClearHistoryAsync(BrowserProfile profile, IReadOnlyList<string> targetPaths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.ProfileDirectory))
        {
            return Task.FromResult(new BrowserCleanupResult(BrowserCleanupResultStatus.Skipped, "Browser profile directory is empty."));
        }

        if (!Directory.Exists(profile.ProfileDirectory))
        {
            return Task.FromResult(new BrowserCleanupResult(BrowserCleanupResultStatus.Skipped, $"Browser profile directory not found: {profile.ProfileDirectory}"));
        }

        return InvokeOnDispatcherAsync(() => profile.Kind switch
        {
            BrowserKind.Edge => ClearEdgeHistoryInternalAsync(profile.ProfileDirectory, cancellationToken),
            BrowserKind.Chrome => Task.FromResult(ClearChromiumHistoryFiles(profile.ProfileDirectory, targetPaths)),
            _ => Task.FromResult(new BrowserCleanupResult(BrowserCleanupResultStatus.Skipped, "Unsupported browser profile."))
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispatcher.InvokeAsync(() =>
        {
            lock (_hostLock)
            {
                _hostSource?.Dispose();
                _hostSource = null;
            }
        });
    }

    private async Task<BrowserCleanupResult> ClearEdgeHistoryInternalAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CoreWebView2Environment? environment = null;
        CoreWebView2Controller? controller = null;

        try
        {
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory).ConfigureAwait(true);
            controller = await environment.CreateCoreWebView2ControllerAsync(EnsureHostWindowHandle()).ConfigureAwait(true);
            controller.IsVisible = false;

            cancellationToken.ThrowIfCancellationRequested();

            var profile = controller.CoreWebView2.Profile;
            await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory).ConfigureAwait(true);

            var profileLabel = string.IsNullOrWhiteSpace(profile.ProfileName)
                ? Path.GetFileName(profileDirectory)
                : profile.ProfileName;

            return new BrowserCleanupResult(BrowserCleanupResultStatus.Success, $"Cleared Microsoft Edge browsing history for profile '{profileLabel}'.");
        }
        catch (Exception ex)
        {
            return new BrowserCleanupResult(BrowserCleanupResultStatus.Failed, ex.Message, ex);
        }
        finally
        {
            controller?.Close();
        }
    }

    private Task<BrowserCleanupResult> InvokeOnDispatcherAsync(Func<Task<BrowserCleanupResult>> work)
    {
        if (_dispatcher.CheckAccess())
        {
            return work();
        }

        var tcs = new TaskCompletionSource<BrowserCleanupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var result = await work().ConfigureAwait(true);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private IntPtr EnsureHostWindowHandle()
    {
        lock (_hostLock)
        {
            if (_hostSource is { IsDisposed: false })
            {
                return _hostSource.Handle;
            }

            var parameters = new HwndSourceParameters("BrowserCleanupHost")
            {
                Width = 1,
                Height = 1,
                PositionX = -10000,
                PositionY = -10000,
                WindowStyle = unchecked((int)(WindowStyles.WS_DISABLED | WindowStyles.WS_POPUP)),
                UsesPerPixelOpacity = false
            };

            _hostSource = new HwndSource(parameters);
            return _hostSource.Handle;
        }
    }

    private static BrowserCleanupResult ClearChromiumHistoryFiles(string profileDirectory, IReadOnlyList<string> targetPaths)
    {
        if (targetPaths is null || targetPaths.Count == 0)
        {
            return new BrowserCleanupResult(BrowserCleanupResultStatus.Skipped, "No history entries selected for this profile.");
        }

        var successes = 0;
        var failures = new List<string>();

        foreach (var path in targetPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!path.StartsWith(profileDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            if (TryDeleteFileWithRetry(path))
            {
                successes++;
            }
            else
            {
                failures.Add(path);
            }
        }

        if (successes == 0 && failures.Count == 0)
        {
            return new BrowserCleanupResult(BrowserCleanupResultStatus.Skipped, "History files already removed.");
        }

        if (failures.Count > 0)
        {
            var message = $"Failed to clear some history files ({failures.Count} item(s)). Close the browser and try again.";
            return new BrowserCleanupResult(BrowserCleanupResultStatus.Failed, message);
        }

        return new BrowserCleanupResult(BrowserCleanupResultStatus.Success, "Cleared browser history files.");
    }

    private static bool TryDeleteFileWithRetry(string path)
    {
        const int maxAttempts = 2;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            catch
            {
            }

            try
            {
                File.Delete(path);
                return true;
            }
            catch
            {
                if (attempt == maxAttempts - 1)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static class WindowStyles
    {
        public const uint WS_DISABLED = 0x08000000;
        public const uint WS_POPUP = 0x80000000;
    }
}
