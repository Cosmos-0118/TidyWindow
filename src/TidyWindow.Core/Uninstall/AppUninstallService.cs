using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Uninstall;

public sealed class AppUninstallService : IAppUninstallService
{
    private readonly UninstallSandbox _sandbox;

    public AppUninstallService(IUninstallTelemetrySink? telemetrySink = null)
    {
        _sandbox = new UninstallSandbox(telemetrySink);
    }

    public async Task<AppUninstallResult> UninstallAsync(InstalledApp app, AppUninstallOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        var effectiveOptions = options ?? AppUninstallOptions.Default;
        var plan = BuildPlan(app, effectiveOptions);
        var operation = await _sandbox.ExecuteAsync(plan, ExecutePlanStepAsync, cancellationToken).ConfigureAwait(false);
        return new AppUninstallResult(app, operation);
    }

    private UninstallOperationPlan BuildPlan(InstalledApp app, AppUninstallOptions options)
    {
        var steps = new List<UninstallCommandPlan>();
        var requiresElevation = RequiresElevation(app);
        var family = DetectInstallerFamily(app);

        if (!options.WingetOnly)
        {
            if (TryAddMsiStep(app, steps))
            {
                // MSI takes precedence; quiet uninstall usually defers to msiexec anyway.
            }
            else if (TryAddQuietStep(app, family, requiresElevation, steps))
            {
                // quiet path added
            }
            else
            {
                TryAddStandardStep(app, family, requiresElevation, steps);
            }
        }

        if (steps.Count == 0 && !app.HasWingetMetadata)
        {
            throw new InvalidOperationException($"No uninstall strategy available for '{app.Name}'.");
        }

        if ((options.EnableWingetFallback || options.WingetOnly) && app.HasWingetMetadata)
        {
            steps.Add(UninstallCommandPlan.ForWinget(app.WingetId!));
        }

        if (steps.Count == 0)
        {
            throw new InvalidOperationException($"Winget fallback requested but no winget identifier was found for '{app.Name}'.");
        }

        var metadata = BuildMetadata(app, options.MetadataOverrides);

        return new UninstallOperationPlan(
            displayName: app.Name,
            publisher: app.Publisher,
            version: app.Version,
            dryRun: options.DryRun,
            steps: steps,
            operationId: options.OperationId,
            metadata: metadata);
    }

    private static bool TryAddMsiStep(InstalledApp app, ICollection<UninstallCommandPlan> steps)
    {
        if (!app.IsWindowsInstaller || string.IsNullOrWhiteSpace(app.ProductCode))
        {
            return false;
        }

        if (!Guid.TryParse(app.ProductCode, out var productCode))
        {
            return false;
        }

        steps.Add(UninstallCommandPlan.ForMsiexec(productCode));
        return true;
    }

    private static bool TryAddQuietStep(InstalledApp app, InstallerFamily family, bool requiresElevation, ICollection<UninstallCommandPlan> steps)
    {
        if (!app.HasQuietUninstall || string.IsNullOrWhiteSpace(app.QuietUninstallString))
        {
            return false;
        }

        var commandLine = ApplySilentArguments(app.QuietUninstallString, family);
        steps.Add(UninstallCommandPlan.ForQuietUninstall(commandLine, requiresElevation));
        return true;
    }

    private static bool TryAddStandardStep(InstalledApp app, InstallerFamily family, bool requiresElevation, ICollection<UninstallCommandPlan> steps)
    {
        if (string.IsNullOrWhiteSpace(app.UninstallString))
        {
            return false;
        }

        var commandLine = ApplySilentArguments(app.UninstallString, family);
        steps.Add(UninstallCommandPlan.ForStandardUninstall(commandLine, requiresElevation));
        return true;
    }

    private static bool RequiresElevation(InstalledApp app)
    {
        if (app.SourceTags.Any(static tag => string.Equals(tag, "User", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(app.RegistryKey)
            && app.RegistryKey.Contains("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(InstalledApp app, IReadOnlyDictionary<string, string>? overrides)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RegistryKey"] = app.RegistryKey ?? string.Empty,
            ["InstallerType"] = app.InstallerType ?? string.Empty,
            ["ProductCode"] = app.ProductCode ?? string.Empty,
            ["WingetId"] = app.WingetId ?? string.Empty,
            ["SourceTags"] = string.Join(',', app.SourceTags)
        };

        if (app.Metadata is { Count: > 0 })
        {
            foreach (var kvp in app.Metadata)
            {
                metadata[$"App.{kvp.Key}"] = kvp.Value ?? string.Empty;
            }
        }

        if (overrides is not null)
        {
            foreach (var kvp in overrides)
            {
                metadata[kvp.Key] = kvp.Value ?? string.Empty;
            }
        }

        return new ReadOnlyDictionary<string, string>(metadata);
    }

    private async Task<UninstallExecutionResult> ExecutePlanStepAsync(UninstallCommandPlan plan, CancellationToken cancellationToken)
    {
        var outputLines = new List<string>();
        var errorLines = new List<string>();
        var arguments = BuildArgumentString(plan.Arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = plan.Command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            },
            EnableRaisingEvents = false
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            errorLines.Add(ex.Message);
            return new UninstallExecutionResult(-1, new ReadOnlyCollection<string>(outputLines), new ReadOnlyCollection<string>(errorLines), "Failed to start process.");
        }

        using var ctsRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill failures.
            }
        });

        var readOutputTask = CaptureAsync(process.StandardOutput, outputLines, cancellationToken);
        var readErrorTask = CaptureAsync(process.StandardError, errorLines, cancellationToken);

        try
        {
            await Task.WhenAll(readOutputTask, readErrorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errorLines.Add(ex.Message);
        }

        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new UninstallExecutionResult(
            exitCode,
            new ReadOnlyCollection<string>(outputLines),
            new ReadOnlyCollection<string>(errorLines));
    }

    private static async Task CaptureAsync(StreamReader reader, ICollection<string> destination, CancellationToken cancellationToken)
    {
        string? line;
        while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            destination.Add(line);
        }
    }

    private static string BuildArgumentString(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            var argument = arguments[i] ?? string.Empty;
            builder.Append(argument.Contains(' ')
                ? $"\"{argument}\""
                : argument);
        }

        return builder.ToString();
    }

    private static string ApplySilentArguments(string commandLine, InstallerFamily family)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return commandLine;
        }

        var silentArgs = family switch
        {
            InstallerFamily.InnoSetup => new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" },
            InstallerFamily.Nsis => new[] { "/S" },
            InstallerFamily.InstallShield => new[] { "/s" },
            _ => Array.Empty<string>()
        };

        if (silentArgs.Length == 0)
        {
            return commandLine;
        }

        var builder = new StringBuilder(commandLine.Trim());
        foreach (var arg in silentArgs)
        {
            if (ContainsArgument(builder, arg))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(arg);
        }

        return builder.ToString();
    }

    private static bool ContainsArgument(StringBuilder builder, string argument)
    {
        if (builder.Length == 0 || string.IsNullOrWhiteSpace(argument))
        {
            return false;
        }

        return builder.ToString().IndexOf(argument, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static InstallerFamily DetectInstallerFamily(InstalledApp app)
    {
        foreach (var token in EnumerateInstallerTokens(app))
        {
            if (token.Contains("inno", StringComparison.OrdinalIgnoreCase))
            {
                return InstallerFamily.InnoSetup;
            }

            if (token.Contains("nsis", StringComparison.OrdinalIgnoreCase))
            {
                return InstallerFamily.Nsis;
            }

            if (token.Contains("installshield", StringComparison.OrdinalIgnoreCase))
            {
                return InstallerFamily.InstallShield;
            }
        }

        if (LooksLikeInnoBinary(app.UninstallString) || LooksLikeInnoBinary(app.QuietUninstallString))
        {
            return InstallerFamily.InnoSetup;
        }

        return InstallerFamily.Unknown;
    }

    private static IEnumerable<string> EnumerateInstallerTokens(InstalledApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.InstallerType))
        {
            yield return app.InstallerType;
        }

        foreach (var hint in app.InstallerHints)
        {
            if (!string.IsNullOrWhiteSpace(hint))
            {
                yield return hint;
            }
        }
    }

    private static bool LooksLikeInnoBinary(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        try
        {
            var trimmed = commandLine.Trim();
            var firstSpace = trimmed.IndexOf(' ');
            var executable = firstSpace >= 0 ? trimmed[..firstSpace] : trimmed;
            var fileName = Path.GetFileName(executable);
            return fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private enum InstallerFamily
    {
        Unknown,
        InnoSetup,
        Nsis,
        InstallShield
    }
}
