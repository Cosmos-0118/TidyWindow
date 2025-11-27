using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Uninstall;

public interface IAppInventoryService
{
    Task<AppInventorySnapshot> GetInventoryAsync(AppInventoryOptions? options = null, CancellationToken cancellationToken = default);
}
