using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TidyWindow.Next.Core;

public sealed record RecoveryResult(bool Success, ManagerStatus Status, string Message, string? LogDirectory, bool Changed = false);

public sealed class ManagerRecoveryService
{
    private readonly PackageService packages;
    private readonly ManagerHealthService health;
    private readonly ICommandRunner runner;
    private readonly IExecutableLocator locator;
    private readonly Func<ManagerKind, ManagerLocation> locations;
    private readonly string jobsRoot;

    public ManagerRecoveryService(PackageService packages, ICommandRunner runner, IExecutableLocator locator,
        Func<ManagerKind, ManagerLocation>? locations = null, string? jobsRoot = null)
    {
        this.packages = packages; this.runner = runner; this.locator = locator;
        health = new(packages, runner, locator);
        this.locations = locations ?? new ManagerLocations().Get;
        this.jobsRoot = jobsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Recovery");
    }

    public async Task<RecoveryResult> RecoverAsync(ManagerKind manager, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!await packages.OperationGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("Another setup or installation is running.");
        ManagerStatus? before = null;
        string? job = null;
        try
        {
            progress?.Report("Checking the installation and source…");
            before = await health.CheckAsync(manager, true, token).ConfigureAwait(false);
            if (before.Health == ManagerHealth.Ready) return new(true, before, "Everything is working. No changes were needed.", null);
            if (before.Health is ManagerHealth.Restricted or ManagerHealth.ConnectionIssue)
                return new(false, before, before.Health == ManagerHealth.Restricted ? "This operation is restricted by Windows or your organization. No settings were overridden." : "The source is unreachable. Check the connection, then retry. The installation was left intact.", null);
            var location = locations(manager);
            if (manager != ManagerKind.Winget) ManagerLocations.ValidateRoot(location.Root);
            job = Path.Combine(jobsRoot, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            await File.WriteAllTextAsync(Path.Combine(job, "before.json"), JsonSerializer.Serialize(before), token).ConfigureAwait(false);
            progress?.Report("Saving package records and configuration…");
            await Task.Run(() => BackupRecords(manager, location, job), token).ConfigureAwait(false);
            progress?.Report(before.Health == ManagerHealth.Missing ? "Installing from the official source…" : "Repairing the package manager…");
            var command = CreateCommand(before, location, job);
            var result = await runner.RunAsync(command, token).ConfigureAwait(false);
            progress?.Report("Verifying the installation and source…");
            var after = await health.CheckAsync(manager, true, token).ConfigureAwait(false);
            // Registration/environment changes can take a moment to become visible.
            if (result.ExitCode == 0 && after.Health != ManagerHealth.Ready)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                after = await health.CheckAsync(manager, true, token).ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(Path.Combine(job, "after.json"), JsonSerializer.Serialize(after), token).ConfigureAwait(false);
            var success = result.ExitCode == 0 && after.Health == ManagerHealth.Ready;
            var message = success ? $"{after.Definition.Name} is ready." : result.ExitCode == -1 ? "Administrator permission was declined. You can retry when ready." : "Setup could not be completed. Details and backups are available below.";
            if (!success)
            {
                var detail = result.ExitCode == 0 ? after.Detail : result.Error + "\n" + result.Output;
                var structured = Path.Combine(job, "result.json");
                if (File.Exists(structured))
                {
                    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(structured, token).ConfigureAwait(false));
                    if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString())) detail = error.GetString()!;
                }
                after = ManagerHealthService.Classify(after, detail.Trim().Length > 0 ? detail : after.Detail, after.Problem);
            }
            return new(success, after, message, job, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            before ??= new(ManagerDefinition.All.Single(x => x.Kind == manager), ManagerHealth.Unavailable, "", locator.Find(manager), ex.Message);
            return new(false, ManagerHealthService.Classify(before, ex.Message, before.Problem), ex.Message, job);
        }
        finally { packages.OperationGate.Release(); }
    }

    public CommandSpec CreateCommand(ManagerStatus status, ManagerLocation location, string job)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var shell = status.Definition.Kind is ManagerKind.Winget or ManagerKind.Chocolatey
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : locator.PowerShell;
        var variables = $"$root={Literal(location.Root)}; $configuration={Literal(location.Configuration)}; $job={Literal(job)}; $problem={Literal(status.Problem.ToString())}; $executable={Literal(status.Executable ?? "")}; $powershell={Literal(shell)};\n";
        var script = variables + Resource("Common") + "\n" + Resource(status.Definition.Kind.ToString()) + "\n" + Resource("Complete");
        var elevate = status.Definition.Kind == ManagerKind.Chocolatey || status.Definition.Kind == ManagerKind.Winget && status.Problem == ManagerProblem.Source;
        return new(shell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromMinutes(10), elevate);
    }

    private static string Resource(string name)
    {
        var assembly = typeof(ManagerRecoveryService).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Recovery." + name + ".ps1") ?? throw new InvalidOperationException("Recovery script is missing: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void BackupRecords(ManagerKind manager, ManagerLocation location, string job)
    {
        if (manager == ManagerKind.Winget) return;
        if (File.Exists(location.Configuration)) File.Copy(location.Configuration, Path.Combine(job, "manager-config.backup"));
        if (manager == ManagerKind.Chocolatey)
        {
            var config = Path.Combine(location.Root, "config");
            if (Directory.Exists(config))
                foreach (var file in Directory.EnumerateFiles(config)) File.Copy(file, Path.Combine(job, "config-" + Path.GetFileName(file)));
            var records = Path.Combine(location.Root, "lib");
            if (Directory.Exists(records))
                foreach (var directory in Directory.EnumerateDirectories(records))
                foreach (var file in Directory.EnumerateFiles(directory, "*.nuspec"))
                {
                    var target = Path.Combine(job, "records", Path.GetFileName(directory));
                    Directory.CreateDirectory(target);
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                }
        }
        else
        {
            var apps = Path.Combine(location.Root, "apps");
            if (Directory.Exists(apps))
                foreach (var app in Directory.EnumerateDirectories(apps))
                foreach (var name in new[] { "install.json", "manifest.json" })
                {
                    var file = Path.Combine(app, "current", name);
                    if (!File.Exists(file)) continue;
                    var target = Path.Combine(job, "records", Path.GetFileName(app));
                    Directory.CreateDirectory(target);
                    File.Copy(file, Path.Combine(target, name));
                }
        }
    }
}
