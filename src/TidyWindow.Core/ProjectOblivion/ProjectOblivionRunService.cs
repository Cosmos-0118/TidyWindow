using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.ProjectOblivion;

public sealed class ProjectOblivionRunService
{
    private const string ScriptRelativePath = "automation/scripts/uninstall-app-deep.ps1";
    private const string OverrideEnvironmentVariable = "TIDYWINDOW_OBLIVION_RUN_SCRIPT";

    public async IAsyncEnumerable<ProjectOblivionRunEvent> RunAsync(
        ProjectOblivionRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            throw new ArgumentException("AppId is required for Project Oblivion runs.", nameof(request));
        }

        var scriptPath = ResolveScriptPath();
        var pwshPath = LocatePowerShellExecutable();
        var arguments = BuildArgumentList(scriptPath, request);

        var startInfo = new ProcessStartInfo
        {
            FileName = pwshPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = false };
        process.Start();

        using var registration = cancellationToken.Register(() =>
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
                // Suppress kill failures.
            }
        });

        var errorLines = new List<string>();
        var errorTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lock (errorLines)
                    {
                        errorLines.Add(line.Trim());
                    }
                }
            }
        }, cancellationToken);

        string? outputLine;
        while ((outputLine = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(outputLine))
            {
                continue;
            }

            if (TryParseEvent(outputLine, out var evt))
            {
                yield return evt;
            }
            else
            {
                yield return new ProjectOblivionRunEvent("raw", DateTimeOffset.UtcNow, null, outputLine);
            }
        }

        await errorTask.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (process.ExitCode != 0)
        {
            var detail = errorLines.Count > 0 ? string.Join(Environment.NewLine, errorLines) : "Unknown error.";
            throw new InvalidOperationException($"Project Oblivion run failed with exit code {process.ExitCode}: {detail}");
        }
    }

    private static bool TryParseEvent(string line, out ProjectOblivionRunEvent evt)
    {
        evt = default!;
        try
        {
            var node = JsonNode.Parse(line)?.AsObject();
            if (node is null)
            {
                return false;
            }

            var type = node["type"]?.GetValue<string>() ?? "unknown";
            var timestampText = node["timestamp"]?.GetValue<string>();
            node.Remove("type");
            node.Remove("timestamp");

            DateTimeOffset timestamp;
            if (!DateTimeOffset.TryParse(timestampText, out timestamp))
            {
                timestamp = DateTimeOffset.UtcNow;
            }

            evt = new ProjectOblivionRunEvent(type, timestamp, node, line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildArgumentList(string scriptPath, ProjectOblivionRunRequest request)
    {
        var builder = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            Quote(scriptPath),
            "-AppId",
            Quote(request.AppId)
        };

        if (!string.IsNullOrWhiteSpace(request.InventoryPath))
        {
            builder.Add("-InventoryPath");
            builder.Add(Quote(request.InventoryPath!));
        }

        if (!string.IsNullOrWhiteSpace(request.SelectionPath))
        {
            builder.Add("-SelectionPath");
            builder.Add(Quote(request.SelectionPath!));
        }

        if (request.AutoSelectAll)
        {
            builder.Add("-AutoSelectAll:$true");
        }

        if (request.WaitForSelection)
        {
            builder.Add("-WaitForSelection:$true");
        }

        if (request.SelectionTimeoutSeconds > 0)
        {
            builder.Add("-SelectionTimeoutSeconds");
            builder.Add(request.SelectionTimeoutSeconds.ToString());
        }

        if (request.DryRun)
        {
            builder.Add("-DryRun:$true");
        }

        return string.Join(' ', builder);
    }

    private static string Quote(string value)
    {
        return value.Contains(' ')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    private static string ResolveScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, ScriptRelativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, ScriptRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate automation script at '{ScriptRelativePath}'.");
    }

    private static string LocatePowerShellExecutable()
    {
        try
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paths is not null)
            {
                foreach (var path in paths)
                {
                    var candidate = Path.Combine(path, "pwsh.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch
        {
            // Ignore PATH probing issues and fall back to default.
        }

        return "pwsh";
    }
}
