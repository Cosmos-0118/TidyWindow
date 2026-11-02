using System.Runtime.Versioning;
using System.Security.Principal;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace TidyWindow.Next.Tests;

/// <summary>
/// The environment variables that switch on tests which look at the real PC. Each must be set to "1". A plain [LiveFact] asks for
/// <see cref="Live"/>; name other variables to ask for those instead, and every named one must be set.
/// </summary>
public static class LiveGate
{
    /// <summary>Read-only looks at this PC: its packages, services, registry, installed products and caches (copies of which are cleaned).</summary>
    public const string Live = "TIDYWINDOW_LIVE_TESTS";

    /// <summary>Downloads one vendor installer and unpacks it in a temp folder. Ask for it together with <see cref="Live"/>.</summary>
    public const string Download = "TIDYWINDOW_LIVE_DOWNLOAD";

    /// <summary>Copies the real Scoop into an isolated root under TIDYWINDOW_TEST_ARTIFACTS and runs the recovery scripts on the copy.</summary>
    public const string Recovery = "TIDYWINDOW_RECOVERY_TESTS";

    /// <summary>Runs the privileged repair path on a throwaway registry tree. Needs an administrator window as well as the variable.</summary>
    public const string Elevated = "TIDYWINDOW_ELEVATED_TESTS";

    /// <summary>Why a test behind these variables is not run now, or null when it can run.</summary>
    internal static string? WhySkipped(IReadOnlyList<string> variables)
    {
        if (!OperatingSystem.IsWindows()) return "Looks at a Windows PC; needs Windows.";
        var missing = variables.Where(variable => Environment.GetEnvironmentVariable(variable) != "1").ToList();
        if (missing.Count > 0) return "Looks at the real PC. Set " + string.Join("=1 and ", missing) + "=1 to run it.";
        if (variables.Contains(Elevated) && !IsAdministrator()) return "Needs an administrator window: " + Elevated + " is set, but this process is not elevated.";
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

/// <summary>
/// A [Fact] that looks at the real PC. Unless the named environment variables (by default TIDYWINDOW_LIVE_TESTS) are "1", the test is
/// reported as skipped with the reason, so a run on a machine without them cannot pass by doing nothing. Each such test also carries the
/// trait Category=Live: <c>dotnet test --filter "Category!=Live"</c> leaves them out and <c>--filter "Category=Live"</c> runs only them.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[TraitDiscoverer(LiveTraitDiscoverer.TypeName, LiveTraitDiscoverer.AssemblyName)]
public sealed class LiveFactAttribute : FactAttribute, ITraitAttribute
{
    /// <param name="variables">The environment variables that must be "1". None means <see cref="LiveGate.Live"/>.</param>
    public LiveFactAttribute(params string[] variables)
    {
        Variables = variables.Length == 0 ? [LiveGate.Live] : variables;
        Skip = LiveGate.WhySkipped(Variables);
    }

    public IReadOnlyList<string> Variables { get; }
}

/// <summary>The [Theory] counterpart of <see cref="LiveFactAttribute"/>, for a live test with [InlineData] rows.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[TraitDiscoverer(LiveTraitDiscoverer.TypeName, LiveTraitDiscoverer.AssemblyName)]
public sealed class LiveTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    /// <param name="variables">The environment variables that must be "1". None means <see cref="LiveGate.Live"/>.</param>
    public LiveTheoryAttribute(params string[] variables)
    {
        Variables = variables.Length == 0 ? [LiveGate.Live] : variables;
        Skip = LiveGate.WhySkipped(Variables);
    }

    public IReadOnlyList<string> Variables { get; }
}

/// <summary>Gives every [LiveFact] and [LiveTheory] the trait Category=Live, so the filter above works without a second attribute.</summary>
public sealed class LiveTraitDiscoverer : ITraitDiscoverer
{
    public const string TypeName = "TidyWindow.Next.Tests.LiveTraitDiscoverer";
    public const string AssemblyName = "TidyWindow.Tests";

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "Live");
    }
}
