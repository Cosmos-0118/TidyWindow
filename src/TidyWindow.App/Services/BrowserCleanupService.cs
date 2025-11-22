using System;
using System.IO;
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

public interface IBrowserCleanupService : IDisposable
{
    Task<BrowserCleanupResult> ClearEdgeHistoryAsync(string profileDirectory, CancellationToken cancellationToken);
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

    public Task<BrowserCleanupResult> ClearEdgeHistoryAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            return Task.FromResult(new BrowserCleanupResult(BrowserCleanupResultStatus.Skipped, "Edge profile directory is empty."));
        }

        if (!Directory.Exists(profileDirectory))
        {
            return Task.FromResult(new BrowserCleanupResult(BrowserCleanupResultStatus.Skipped, $"Edge profile directory not found: {profileDirectory}"));
        }

        return InvokeOnDispatcherAsync(() => ClearEdgeHistoryInternalAsync(profileDirectory, cancellationToken));
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

    private static class WindowStyles
    {
        public const uint WS_DISABLED = 0x08000000;
        public const uint WS_POPUP = 0x80000000;
    }
}
