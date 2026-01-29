using System.Threading.Tasks;

namespace TidyWindow.App.Services;

public interface ILatencyGuardSampler
{
    Task<LatencyGuardSampler.LatencySample> SampleAsync();
}
