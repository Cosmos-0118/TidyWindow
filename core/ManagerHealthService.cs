using System.ComponentModel;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public sealed class ManagerHealthService(PackageService packages, ICommandRunner runner, IExecutableLocator locator)
{
    public async Task<ManagerStatus> CheckAsync(ManagerKind kind, bool checkSource = false, CancellationToken token = default)
    {
        var definition = ManagerDefinition.All.Single(x => x.Kind == kind);
        var path = locator.Find(kind);
        if (path is null) return new(definition, ManagerHealth.Missing, "", null, "Not installed on this computer.", ManagerProblem.NotInstalled);
        var status = new ManagerStatus(definition, ManagerHealth.Ready, "", path, "Ready");
        try
        {
            var version = await runner.RunAsync(packages.BuildCommand(kind, path, ["--version"], TimeSpan.FromSeconds(8)), token).ConfigureAwait(false);
            if (version.ExitCode != 0) return Failed(status, version, ManagerProblem.Executable);
            var number = Regex.Match(PackageOutputParser.Clean(version.Output), @"v?\d+\.\d+(?:\.[\w.-]+)*").Value;
            if (kind == ManagerKind.Scoop && number.Length == 0)
                number = Regex.Match(PackageOutputParser.Clean(version.Output), @"(?m)^\s*([a-f0-9]{7,40})(?:\s|$)").Groups[1].Value;
            if (number.Length == 0) return status with { Health = ManagerHealth.Unavailable, Problem = ManagerProblem.Executable, Detail = "The manager did not return a valid version." };
            status = status with { Version = number };
            // An executable that prints a version may still have a damaged configuration or package database.
            var local = await packages.ReadInstalledAsync(status, token).ConfigureAwait(false);
            if (local.Error is not null) return Classify(status, local.Error, ManagerProblem.Configuration);
            if (checkSource)
            {
                // Bypass search cache: recovery success must be verified against the actual source.
                packages.ClearSearchCache();
                var source = await packages.SearchAsync(status, "7zip", token).ConfigureAwait(false);
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

    public static ManagerStatus Failed(ManagerStatus status, CommandResult result, ManagerProblem problem)
    {
        var detail = $"Exit {result.ExitCode}: " + PackageOutputParser.Clean(result.Error + "\n" + result.Output).Trim();
        return Classify(status, detail, problem);
    }

    public static ManagerStatus Classify(ManagerStatus status, string detail, ManagerProblem fallback)
    {
        static bool Has(string text, params string[] values) => values.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (Has(detail, "blocked by policy", "Group Policy", "blocked by your administrator", "PSSecurityException", "running scripts is disabled", "-1978335174", "0x8A15003A"))
            return status with { Health = ManagerHealth.Restricted, Problem = ManagerProblem.Policy, Detail = "Windows or your organization restricts this operation. " + detail };
        if (Has(detail, "could not resolve", "No such host", "name resolution", "Unable to connect", "network is unreachable", "network unreachable", "proxy", "certificate", "TLS", "0x80072", "-214701") || fallback == ManagerProblem.Source && Has(detail, "timed out", "did not respond in time"))
            return status with { Health = ManagerHealth.ConnectionIssue, Problem = ManagerProblem.Network, Detail = "The package source could not be reached. " + detail };
        if (Has(detail, "-1978335222", "-1978335221", "-1978335217", "-1978335214", "-1978335211", "-1978335169", "-1978335163", "-1978335157")) fallback = ManagerProblem.Source;
        return status with { Health = ManagerHealth.Unavailable, Problem = fallback, Detail = detail.Length > 1800 ? detail[^1800..] : detail };
    }
}
