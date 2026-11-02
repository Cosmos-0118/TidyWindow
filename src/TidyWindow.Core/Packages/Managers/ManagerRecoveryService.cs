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
    private readonly string protectedRoot;

    /// <param name="elevatedProblem">See <see cref="ManagerHealthService"/>.</param>
    /// <param name="protectedRoot">Where an elevated recovery keeps its own working folder: %ProgramData%\TidyWindow.Next by default, which the script makes administrator-only.</param>
    public ManagerRecoveryService(PackageService packages, ICommandRunner runner, IExecutableLocator locator,
        Func<ManagerKind, ManagerLocation>? locations = null, string? jobsRoot = null, Func<ManagerKind, string, string?>? elevatedProblem = null, string? protectedRoot = null)
    {
        this.packages = packages; this.runner = runner; this.locator = locator;
        health = new(packages, runner, locator, elevatedProblem);
        this.locations = locations ?? new ManagerLocations().Get;
        this.jobsRoot = jobsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Recovery");
        this.protectedRoot = protectedRoot ?? ProtectedStore.DefaultRoot;
    }

    /// <summary>How often an administrator window that outlived its time is looked in on, and how long before it is given up on. Short in tests.</summary>
    public TimeSpan LingerPoll { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan LingerLimit { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The wait for an elevated recovery that could not be stopped when its time ran out, or null. The operation gate stays taken until it ends.</summary>
    public Task? Lingering { get; private set; }

    public async Task<RecoveryResult> RecoverAsync(ManagerKind manager, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!await packages.OperationGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("Another setup or installation is running.");
        ManagerStatus? before = null;
        string? job = null;
        CommandSpec? command = null;
        var keepGate = false;
        try
        {
            progress?.Report("Checking the installation and source…");
            before = await health.CheckAsync(manager, true, token).ConfigureAwait(false);
            if (before.Health == ManagerHealth.Ready) return new(true, before, "Everything is working. No changes were needed.", null);
            if (before.Health is ManagerHealth.Restricted or ManagerHealth.ConnectionIssue)
                return new(false, before, before.Health == ManagerHealth.Restricted ? "This operation is restricted by Windows or your organization. No settings were overridden." : "The source is unreachable. Check the connection, then retry. The installation was left intact.", null);
            if (before.Problem == ManagerProblem.Timeout)
                return new(false, before, "The package manager did not respond in time, which a repair would not change. Try again when the PC or the connection is less busy. The installation was left intact.", null);
            var location = locations(manager);
            if (manager != ManagerKind.Winget) ManagerLocations.ValidateRoot(location.Root);
            job = Path.Combine(jobsRoot, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            await File.WriteAllTextAsync(Path.Combine(job, "before.json"), JsonSerializer.Serialize(before), token).ConfigureAwait(false);
            progress?.Report("Saving package records and configuration…");
            await Task.Run(() => BackupRecords(manager, location, job), token).ConfigureAwait(false);
            progress?.Report(before.Health == ManagerHealth.Missing ? "Installing from the official source…" : "Repairing the package manager…");
            // The runner's notices (the Windows permission prompt, the separate administrator window) are what a person needs to see while this runs.
            command = CreateCommand(before, location, job) with
            { Progress = progress is null ? null : new Progress<CommandOutput>(output => { if (output.Kind == CommandOutputKind.Notice) progress.Report(output.Text); }) };
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
                if (await ReadFailureAsync(job, token).ConfigureAwait(false) is { } reported) detail = reported;
                after = ManagerHealthService.Classify(after, detail.Trim().Length > 0 ? detail : after.Detail, after.Problem);
            }
            return new(success, after, message, job, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (InvalidOperationException ex) when (command is { Elevate: true } && job is not null)
        {
            // The administrator window takes no orders from this process, so it could not be stopped when its time ran out and is still at work. The gate
            // stays taken until its result file appears (or a long wait runs out): releasing it now would let a second repair or an install run beside it.
            keepGate = true;
            Lingering = LingerAsync(job, manager);
            before ??= new(ManagerDefinition.All.Single(x => x.Kind == manager), ManagerHealth.Unavailable, "", locator.Find(manager), ex.Message);
            return new(false, before, "The repair is still running in its administrator window. Other package operations wait until it finishes; check that window.", job, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            before ??= new(ManagerDefinition.All.Single(x => x.Kind == manager), ManagerHealth.Unavailable, "", locator.Find(manager), ex.Message);
            return new(false, ManagerHealthService.Classify(before, ex.Message, before.Problem), ex.Message, job);
        }
        finally { if (!keepGate) packages.OperationGate.Release(); }
    }

    private async Task LingerAsync(string job, ManagerKind manager)
    {
        try
        {
            var limit = DateTimeOffset.UtcNow + LingerLimit;
            while (DateTimeOffset.UtcNow < limit && !ResultFiles(job).Any(File.Exists)) await Task.Delay(LingerPoll).ConfigureAwait(false);
            try { await health.CheckAsync(manager).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        finally { packages.OperationGate.Release(); }
    }

    /// <summary>The error the script reported, from the job folder or, when the elevated run could not write there, from its own protected folder.</summary>
    private async Task<string?> ReadFailureAsync(string job, CancellationToken token)
    {
        foreach (var file in ResultFiles(job).Where(File.Exists))
        {
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, token).ConfigureAwait(false));
                if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString())) return error.GetString();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private IEnumerable<string> ResultFiles(string job) =>
        [Path.Combine(job, "result.json"), Path.Combine(protectedRoot, "Recovery", Path.GetFileName(job), "result.json")];

    public CommandSpec CreateCommand(ManagerStatus status, ManagerLocation location, string job)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var elevate = status.Definition.Kind == ManagerKind.Chocolatey || status.Definition.Kind == ManagerKind.Winget && status.Problem == ManagerProblem.Source;
        // Windows PowerShell for WinGet and Chocolatey: it has the Appx cmdlets and the folder-permission calls, and only administrators can change it.
        // Scoop runs in the locator's host, which is one of the protected hosts too.
        var shell = status.Definition.Kind == ManagerKind.Scoop ? locator.PowerShell : ElevatedProgram.WindowsPowerShell;
        var variables = $"$root={Literal(location.Root)}; $configuration={Literal(location.Configuration)}; $job={Literal(job)}; $problem={Literal(status.Problem.ToString())}; $executable={Literal(status.Executable ?? "")}; $powershell={Literal(shell)};\n";
        // The hardening for administrator rights goes first, before any cmdlet runs; a run without them has no use for it and little room on the command line.
        var script = variables + (elevate ? Resource("Elevated") + "\n" : "") + Resource("Common") + "\n" + Resource(status.Definition.Kind.ToString()) + "\n" + Resource("Complete");
        return new(shell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromMinutes(10), elevate);
    }

    /// <summary>A recovery script, without its comments and indentation: the script travels on the command line, which has a fixed length.</summary>
    internal static string Resource(string name)
    {
        var assembly = typeof(ManagerRecoveryService).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Recovery." + name + ".ps1") ?? throw new InvalidOperationException("Recovery script is missing: " + name);
        using var reader = new StreamReader(stream);
        return string.Join("\n", reader.ReadToEnd().Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')));
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
