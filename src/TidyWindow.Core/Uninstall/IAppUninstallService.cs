using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Uninstall;

public interface IAppUninstallService
{
    Task<AppUninstallResult> UninstallAsync(InstalledApp app, AppUninstallOptions? options = null, CancellationToken cancellationToken = default);
}
