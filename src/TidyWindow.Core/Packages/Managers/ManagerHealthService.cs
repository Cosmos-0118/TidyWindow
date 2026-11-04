using System.ComponentModel;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <param name="elevatedProblem">Why a manager's executable must not be used for runs with administrator rights (kind, path), or null. The default applies <see cref="ElevatedProgram"/> and the Chocolatey account rule; tests that work in temporary folders replace it.</param>
public sealed partial class ManagerHealthService(PackageService packages, ICommandRunner runner, IExecutableLocator locator, Func<ManagerKind, string, string?>? elevatedProblem = null)
{
    private readonly Func<ManagerKind, string, string?> elevatedProblem = elevatedProblem ?? DefaultElevatedProblem;

    /// <summary>Chocolatey is the manager that always runs with administrator rights, so its executable and its PC-wide setting are judged before it is used at all.</summary>
    public static string? DefaultElevatedProblem(ManagerKind kind, string path) =>
        kind == ManagerKind.Chocolatey ? ElevatedProgram.ProtectedPathProblem(path) ?? new ManagerLocations().ChocolateyAccountProblem() : null;

    public async Task<ManagerStatus> CheckAsync(ManagerKind kind, bool checkSource = false, CancellationToken token = default)
    {
        var definition = ManagerDefinition.All.Single(x => x.Kind == kind);
        var path = locator.Find(kind);
        if (path is null) return new(definition, ManagerHealth.Missing, "", null, "Not installed on this computer.", ManagerProblem.NotInstalled);
        var status = new ManagerStatus(definition, ManagerHealth.Ready, "", path, "Ready");
        if (elevatedProblem(kind, path) is { } refused) return status with { Health = ManagerHealth.Unavailable, Problem = ManagerProblem.Executable, Detail = refused };
        try
        {
            var version = await runner.RunAsync(packages.BuildCommand(kind, path, ["--version"], TimeSpan.FromSeconds(8)), token).ConfigureAwait(false);
            if (version.ExitCode != 0) return Failed(status, version, ManagerProblem.Executable);
            var number = kind == ManagerKind.Scoop ? ScoopVersion(version.Output) : Regex.Match(PackageOutputParser.Clean(version.Output), @"v?\d+\.\d+(?:\.[\w.-]+)*").Value;
            if (number.Length == 0) return status with { Health = ManagerHealth.Unavailable, Problem = ManagerProblem.Executable, Detail = "The manager did not return a valid version." };
            status = status with { Version = number };
            // An executable that prints a version may still have a damaged configuration or package database.
            var local = await packages.ReadInstalledAsync(status, token).ConfigureAwait(false);
            if (local.Error is not null) return Classify(status, local.Error, ManagerProblem.Configuration);
            if (checkSource)
            {
                // The source must be reached the way an install reaches it: through the manager's own command line, not through the app's feed, catalog or cache.
                var source = await packages.ProbeSourceAsync(status, "7zip", token).ConfigureAwait(false);
                if (source.Error is not null) return Classify(status, source.Error, ManagerProblem.Source);
                if (source.Packages.Count == 0)
                    return status with { Health = ManagerHealth.Unavailable, Problem = ManagerProblem.Source, Detail = "The source returned no packages for the verification search." };
            }
            return status with { Detail = checkSource ? "Executable, installed-package inventory and package source verified." : "Executable and installed-package inventory verified. Sources are checked when used." };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is TimeoutException or IOException or Win32Exception or UnauthorizedAccessException)
        { return Classify(status, ex.Message, ManagerProblem.Executable); }
    }

    /// <summary>
    /// The version from <c>scoop --version</c>, which prints "Current Scoop version:", then the version (or the newest commit when Scoop follows
    /// another branch), then the newest commit of every bucket. Only the line after the heading is Scoop's own; a bucket's commit line can carry any
    /// package's version number.
    /// </summary>
    internal static string ScoopVersion(string output)
    {
        var lines = PackageOutputParser.Clean(output).Split('\n').Select(x => x.Trim()).ToArray();
        var heading = Array.FindIndex(lines, x => x.StartsWith("Current Scoop version", StringComparison.OrdinalIgnoreCase));
        var line = (heading >= 0 ? lines.Skip(heading + 1) : lines.Take(1)).FirstOrDefault(x => x.Length > 0) ?? "";
        var commit = Regex.Match(line, @"^([a-f0-9]{7,40})(?:\s|$)").Groups[1].Value;
        return commit.Length > 0 ? commit : Regex.Match(line, @"v?\d+\.\d+(?:\.[\w.-]+)*").Value;
    }

    public static ManagerStatus Failed(ManagerStatus status, CommandResult result, ManagerProblem problem)
    {
        var detail = $"Exit {result.ExitCode}: " + PackageOutputParser.Clean(result.Error + "\n" + result.Output).Trim();
        return Classify(status, detail, problem);
    }

    public static ManagerStatus Classify(ManagerStatus status, string detail, ManagerProblem fallback)
    {
        static bool Has(string text, params string[] values) => values.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        detail = Redact(detail);
        // The words are English, so the exception type names and error codes beside them are what hold on a PC in another language (the Scoop host prints them after the message).
        if (Has(detail, "blocked by policy", "Group Policy", "blocked by your administrator", "PSSecurityException", "running scripts is disabled", "-1978335174", "0x8A15003A"))
            return status with { Health = ManagerHealth.Restricted, Problem = ManagerProblem.Policy, Detail = "Windows or your organization restricts this operation. " + detail };
        if (Has(detail, "could not resolve", "No such host", "name resolution", "Unable to connect", "network is unreachable", "network unreachable", "proxy", "certificate", "TLS", "0x80072", "-214701", "WebException", "SocketException", "HttpRequestException") || fallback == ManagerProblem.Source && Has(detail, "timed out"))
            return status with { Health = ManagerHealth.ConnectionIssue, Problem = ManagerProblem.Network, Detail = "The package source could not be reached. " + detail };
        // Not answering in time is a slow PC or a slow link, not a damaged installation: nothing here is worth repairing.
        if (Has(detail, "did not respond in time", "timed out"))
            return status with { Health = ManagerHealth.Unavailable, Problem = ManagerProblem.Timeout, Detail = "The package manager did not respond in time. Try again later. " + detail };
        if (Has(detail, "-1978335222", "-1978335221", "-1978335217", "-1978335214", "-1978335211", "-1978335169", "-1978335163", "-1978335157")) fallback = ManagerProblem.Source;
        return status with { Health = ManagerHealth.Unavailable, Problem = fallback, Detail = detail.Length > 1800 ? detail[^1800..] : detail };
    }

    /// <summary>Error text can echo a proxy address with a password in it, or a token from a configuration; neither belongs on screen or in the log.</summary>
    internal static string Redact(string text) => Secret().Replace(Credentials().Replace(text, "***:***@"), "$1=***");

    [GeneratedRegex(@"(?<=://)[^/\s@:]+:[^/\s@]+@")]
    private static partial Regex Credentials();

    [GeneratedRegex(@"\b(password|passwd|pwd|token|apikey|api[-_]key|secret)\s*[=:]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();
}
