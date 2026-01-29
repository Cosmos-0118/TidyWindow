using System.Threading.Tasks;

namespace TidyWindow.App.Services;

public interface ILatencyGuardProfileService
{
    Task<LatencyGuardProfileState> GetStateAsync();
    Task<LatencyGuardProfileState> ApplyAsync(bool trimEffects, bool trimRefreshRate, bool throttleModels);
    Task<LatencyGuardProfileState> RevertAsync();
    bool? IsHagsEnabled();
    bool SetHags(bool enabled);
    OllamaConfig GetOllamaConfig();
    void SetOllamaConfig(int? numCtx, int? numParallel);
    bool IsOllamaRunning();
    void RestartOllama();
}
