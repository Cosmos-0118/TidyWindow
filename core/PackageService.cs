using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public sealed class PackageService(ICommandRunner runner, IExecutableLocator locator)
{
    private readonly SemaphoreSlim installGate = new(1, 1);
    internal SemaphoreSlim OperationGate => installGate;
    private readonly PackageSearchCache searchCache = new();
    public void ClearSearchCache() => searchCache.Clear();
    public async Task<InstalledInventory> GetInstalledAsync(IReadOnlyList<ManagerStatus> managers, CancellationToken token = default)
    {
        var tasks = managers.Where(x => x.Health == ManagerHealth.Ready).Select(x => ReadInstalledAsync(x, token)).ToArray();
        var windows = await Task.Run(() => OperatingSystem.IsWindows() ? WindowsInstalledApps.Read() : (Apps: (IReadOnlyList<InstalledApp>)Array.Empty<InstalledApp>(), Errors: (IReadOnlyList<string>)Array.Empty<string>()), token).ConfigureAwait(false);
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new InstalledInventory(results.SelectMany(x => x.Apps).Concat(windows.Apps),
            results.Where(x => x.Error is not null).Select(x => $"{x.Manager}: Installed status could not be checked. {x.Error}").Concat(windows.Errors));
    }

    public async Task<InventoryResult> ReadInstalledAsync(ManagerStatus manager, CancellationToken token = default)
    {
        var kind = manager.Definition.Kind;
        if (manager.Health != ManagerHealth.Ready || manager.Executable is null) return new(kind, [], "Manager is unavailable.");
        string[] arguments = kind switch
        {
            ManagerKind.Winget => ["list", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
            ManagerKind.Chocolatey => ["list", "--limit-output"],
            _ => ["list"]
        };
        try
        {
            var result = await runner.RunAsync(BuildCommand(kind, manager.Executable, arguments, TimeSpan.FromSeconds(20)), token).ConfigureAwait(false);
            if (result.ExitCode == unchecked((int)0x8A150014)) return new(kind, []);
            if (kind == ManagerKind.Scoop && result.ExitCode == 1 && result.Output.Contains("There aren't any apps installed", StringComparison.OrdinalIgnoreCase)) return new(kind, []);
            if (result.ExitCode != 0) return new(kind, [], Failure(result));
            // Inventory must not inherit the search result cap: an installed app can be row 61+.
            var apps = PackageOutputParser.Parse(kind, result.Output, int.MaxValue)
                .Select(x => new InstalledApp(kind, x.Id, x.Name, x.Version)).ToArray();
            var clean = PackageOutputParser.Clean(result.Output);
            if (apps.Length == 0 && !string.IsNullOrWhiteSpace(clean) &&
                !Regex.IsMatch(clean, @"(?m)^\s*-{2,}[\s-]*$|0 packages|no (?:installed |applications |apps |packages)|aren't any installed", RegexOptions.IgnoreCase))
                return new(kind, [], "The installed-package inventory could not be read. The manager may have a damaged configuration or an unsupported output format.");
            return new(kind, apps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(kind, [], ex.Message); }
    }
    public async Task<IReadOnlyList<ManagerStatus>> DetectAsync(CancellationToken token = default) =>
        await Task.WhenAll(ManagerDefinition.All.Select(async definition =>
        {
            var path = locator.Find(definition.Kind);
            if (path is null) return new ManagerStatus(definition, ManagerHealth.Missing, "", null, "Not installed on this device");
            try
            {
                var result = await runner.RunAsync(BuildCommand(definition.Kind, path, ["--version"], TimeSpan.FromSeconds(8)), token).ConfigureAwait(false);
                var version = Regex.Match(PackageOutputParser.Clean(result.Output), @"v?\d+\.\d+(?:\.[\w.-]+)*").Value;
                return result.ExitCode == 0
                    ? new ManagerStatus(definition, ManagerHealth.Ready, version.Length > 0 ? version : "Available", path, "Ready to search and install")
                    : new ManagerStatus(definition, ManagerHealth.Unavailable, "", path, Failure(result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return new ManagerStatus(definition, ManagerHealth.Unavailable, "", path, ex.Message); }
        })).ConfigureAwait(false);

    public async Task<SearchResult> SearchAsync(ManagerStatus manager, string query, CancellationToken token = default)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 100 || query.Any(char.IsControl)) throw new ArgumentException("Enter between 2 and 100 characters.");
        if (manager.Health != ManagerHealth.Ready || manager.Executable is null)
            return new(manager.Definition.Kind, [], "This package manager is not ready. Refresh its status on Overview.");
        return await searchCache.GetAsync($"{manager.Definition.Kind}|{manager.Executable}|{query}",
            cancellation => SearchSourceAsync(manager, query, cancellation), token).ConfigureAwait(false);
    }

    private async Task<SearchResult> SearchSourceAsync(ManagerStatus manager, string query, CancellationToken token)
    {
        var kind = manager.Definition.Kind;
        string[] arguments = kind switch
        {
            ManagerKind.Winget => ["search", "--query", query, "--source", "winget", "--count", "60", "--accept-source-agreements", "--disable-interactivity"],
            ManagerKind.Chocolatey => ["search", query, "--limit-output", "--page-size=60", "--page=0", "--source=https://community.chocolatey.org/api/v2/"],
            _ => ["search", Regex.Escape(query)]
        };
        try
        {
            var command = BuildCommand(kind, manager.Executable!, arguments, TimeSpan.FromSeconds(35));
            var result = await runner.RunAsync(command, token).ConfigureAwait(false);
            // WinGet's documented no-match HRESULT; Scoop uses 1 for an empty search.
            if (result.ExitCode == unchecked((int)0x8A150014) || (kind == ManagerKind.Scoop && result.ExitCode == 1 && result.Output.Contains("No matches found", StringComparison.OrdinalIgnoreCase)))
                return new(kind, []);
            if (result.ExitCode != 0) return new(kind, [], Failure(result));
            var packages = PackageOutputParser.Parse(kind, result.Output);
            if (packages.Count == 0 && !string.IsNullOrWhiteSpace(result.Output) &&
                !result.Output.Contains("No matches", StringComparison.OrdinalIgnoreCase) &&
                !result.Output.Contains("No package", StringComparison.OrdinalIgnoreCase))
                return new(kind, [], "The manager returned no installable rows. Its output format may have changed, or Scoop may require another bucket.");
            return new(kind, packages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(kind, [], ex.Message); }
    }

    public CommandSpec InstallCommand(ManagerStatus manager, Package package, bool interactive = false)
    {
        if (manager.Health != ManagerHealth.Ready || manager.Executable is null || manager.Definition.Kind != package.Manager)
            throw new InvalidOperationException("Refresh package managers before installing.");
        if (!PackageOutputParser.IsSafeId(package.Id)) throw new ArgumentException("The package identifier is incomplete or invalid. Search for its full name.");
        string[] args = package.Manager switch
        {
            ManagerKind.Winget => ["install", "--id", package.Id, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            ManagerKind.Chocolatey => ["install", package.Id, "--yes", "--no-progress", "--source=https://community.chocolatey.org/api/v2/"],
            _ => ["install", package.Id]
        };
        if (interactive && package.Manager != ManagerKind.Scoop) args = [.. args, package.Manager == ManagerKind.Winget ? "--interactive" : "--not-silent"];
        return BuildCommand(package.Manager, manager.Executable, args, interactive ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(20)) with { Elevate = package.Manager == ManagerKind.Chocolatey, MayShowInstaller = true };
    }

    public async Task<CommandResult> InstallAsync(ManagerStatus manager, Package package, CancellationToken token = default, IProgress<CommandOutput>? progress = null, bool interactive = false)
    {
        var command = InstallCommand(manager, package, interactive) with { Progress = progress };
        if (!await installGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("An installation is already running.");
        try { return await runner.RunAsync(command, token).ConfigureAwait(false); }
        finally { installGate.Release(); }
    }

    public static string DisplayInstallCommand(Package package) => package.Manager switch
    {
        ManagerKind.Winget => $"winget install --id {package.Id} --exact --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity",
        ManagerKind.Chocolatey => $"choco install {package.Id} --yes --no-progress --source=https://community.chocolatey.org/api/v2/",
        _ => $"scoop install {package.Id}"
    };

    internal CommandSpec BuildCommand(ManagerKind kind, string path, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        if (kind != ManagerKind.Scoop) return new(path, arguments, timeout);
        // Only Scoop needs a PowerShell host. Arguments are literal strings, never executable user input.
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ErrorActionPreference = 'Stop'; try { & " + Quote(path) + " " + string.Join(" ", arguments.Select(Quote)) + "; if ($LASTEXITCODE) { exit $LASTEXITCODE } } catch { [Console]::Error.WriteLine($_); exit 1 }";
        // Scoop's own launcher sets a process policy. RemoteSigned permits local Scoop scripts
        // without changing the user's saved policy; enterprise Group Policy still takes precedence.
        return new(locator.PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], timeout);
    }

    private static string Failure(CommandResult result)
    {
        var detail = PackageOutputParser.Clean(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim();
        return $"Exit {result.ExitCode}: " + (detail.Length > 1000 ? detail[^1000..] : detail.Length > 0 ? detail : "The package manager could not complete the request.");
    }
}
