using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Processes.AntiSystem;

public enum ThreatIntelVerdict
{
    Unknown = 0,
    KnownGood = 1,
    KnownBad = 2
}

public enum ThreatIntelProviderKind
{
    Local = 0,
    OperatingSystem = 1,
    Remote = 2
}

public enum ThreatIntelMode
{
    Disabled = 0,
    LocalOnly = 1,
    Full = 2
}

public readonly record struct ThreatIntelResult(
    ThreatIntelVerdict Verdict,
    string? Sha256,
    string? Source,
    string? Details)
{
    public static ThreatIntelResult KnownBad(string? sha256, string source, string? details = null)
    {
        return new ThreatIntelResult(ThreatIntelVerdict.KnownBad, sha256, source, details);
    }

    public static ThreatIntelResult KnownGood(string? sha256, string source, string? details = null)
    {
        return new ThreatIntelResult(ThreatIntelVerdict.KnownGood, sha256, source, details);
    }

    public static ThreatIntelResult Unknown(string? sha256, string? details = null)
    {
        return new ThreatIntelResult(ThreatIntelVerdict.Unknown, sha256, null, details);
    }
}

public interface IThreatIntelProvider
{
    ThreatIntelProviderKind Kind { get; }

    ValueTask<ThreatIntelResult> EvaluateAsync(string filePath, string? sha256, CancellationToken cancellationToken);
}
