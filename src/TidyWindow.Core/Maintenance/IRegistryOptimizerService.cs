using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Abstraction for interacting with registry optimizer definitions and automation plans.
/// </summary>
public interface IRegistryOptimizerService
{
    IReadOnlyList<RegistryTweakDefinition> Tweaks { get; }

    IReadOnlyList<RegistryPresetDefinition> Presets { get; }

    RegistryTweakDefinition GetTweak(string tweakId);

    RegistryOperationPlan BuildPlan(IEnumerable<RegistrySelection> selections);

    Task<RegistryOperationResult> ApplyAsync(RegistryOperationPlan plan, CancellationToken cancellationToken = default);

    Task<RegistryRestorePoint?> SaveRestorePointAsync(IEnumerable<RegistrySelection> selections, RegistryOperationPlan plan, CancellationToken cancellationToken = default);

    RegistryRestorePoint? TryGetLatestRestorePoint();

    IReadOnlyList<RegistryRestorePoint> GetAllRestorePoints();

    Task<RegistryOperationResult> ApplyRestorePointAsync(RegistryRestorePoint restorePoint, CancellationToken cancellationToken = default);

    void DeleteRestorePoint(RegistryRestorePoint restorePoint);
}
