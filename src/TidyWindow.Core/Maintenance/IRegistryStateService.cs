using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Provides cached registry state information and probing utilities.
/// </summary>
public interface IRegistryStateService
{
    Task<RegistryTweakState> GetStateAsync(string tweakId, CancellationToken cancellationToken = default);

    Task<RegistryTweakState> GetStateAsync(string tweakId, bool forceRefresh, CancellationToken cancellationToken = default);

    void Invalidate(string? tweakId = null);
}
