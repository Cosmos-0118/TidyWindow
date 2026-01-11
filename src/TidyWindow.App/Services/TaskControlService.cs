using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

/// <summary>
/// Controls Windows Scheduled Tasks (stop/disable) using schtasks. Requires admin.
/// </summary>
public sealed class TaskControlService
{
    private const string SchTasksExe = "schtasks";

    public async Task<TaskControlResult> StopAndDisableAsync(string taskPattern)
    {
        if (!OperatingSystem.IsWindows())
        {
            return TaskControlResult.CreateFailure("Task control is only supported on Windows.");
        }

        var matches = await QueryTasksAsync(taskPattern).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return TaskControlResult.CreateNotFound("No tasks matched this pattern on this PC.");
        }

        var actions = new List<string>();
        foreach (var task in matches)
        {
            var endResult = await RunSchTasksAsync($"/End /TN \"{task}\"").ConfigureAwait(false);
            if (endResult.AccessDenied)
            {
                return TaskControlResult.CreateAccessDenied($"Access denied for {task}.");
            }

            var disableResult = await RunSchTasksAsync($"/Change /Disable /TN \"{task}\"").ConfigureAwait(false);
            if (disableResult.AccessDenied)
            {
                return TaskControlResult.CreateAccessDenied($"Access denied for {task}.");
            }

            if (!disableResult.Success)
            {
                return TaskControlResult.CreateFailure(disableResult.Error ?? "Failed to disable task.");
            }

            actions.Add($"Disabled {task}");
        }

        return TaskControlResult.CreateSuccess(actions);
    }

    private static async Task<IReadOnlyList<string>> QueryTasksAsync(string pattern)
    {
        var regex = BuildWildcardRegex(pattern);
        var queryResult = await RunSchTasksAsync("/Query /FO CSV /NH");
        if (queryResult.AccessDenied)
        {
            return Array.Empty<string>();
        }

        var text = queryResult.Output ?? string.Empty;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<string>();
        foreach (var line in lines)
        {
            var taskName = ParseTaskNameFromCsv(line);
            if (string.IsNullOrWhiteSpace(taskName))
            {
                continue;
            }

            if (regex.IsMatch(taskName))
            {
                matches.Add(taskName);
            }
        }

        return matches;
    }

    private static string ParseTaskNameFromCsv(string csvLine)
    {
        // schtasks CSV: "TaskName","Next Run Time","Status",...
        if (string.IsNullOrWhiteSpace(csvLine))
        {
            return string.Empty;
        }

        var firstComma = csvLine.IndexOf(',');
        if (firstComma <= 0)
        {
            return TrimQuotes(csvLine);
        }

        return TrimQuotes(csvLine[..firstComma]);
    }

    private static string TrimQuotes(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private static async Task<SchTasksResult> RunSchTasksAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SchTasksExe,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start schtasks.");
        }

        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        var accessDenied = (error?.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        var success = process.ExitCode == 0;
        return new SchTasksResult(success, accessDenied, string.IsNullOrWhiteSpace(output) ? null : output.Trim(), string.IsNullOrWhiteSpace(error) ? null : error.Trim());
    }

    private static Regex BuildWildcardRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "*";
        }

        var escaped = Regex.Escape(pattern.Trim());
        var regexPattern = "^" + escaped.Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}

public sealed record TaskControlResult(bool Success, bool NotFound, bool AccessDenied, string Message, IReadOnlyList<string> Actions)
{
    public static TaskControlResult CreateSuccess(IReadOnlyList<string> actions) => new(true, false, false, actions.Count == 0 ? "Tasks stopped/disabled." : string.Join("; ", actions), actions);

    public static TaskControlResult CreateFailure(string message) => new(false, false, false, string.IsNullOrWhiteSpace(message) ? "Task operation failed." : message.Trim(), Array.Empty<string>());

    public static TaskControlResult CreateNotFound(string message) => new(false, true, false, string.IsNullOrWhiteSpace(message) ? "No tasks matched." : message.Trim(), Array.Empty<string>());

    public static TaskControlResult CreateAccessDenied(string message) => new(false, false, true, string.IsNullOrWhiteSpace(message) ? "Access denied." : message.Trim(), Array.Empty<string>());
}

internal sealed record SchTasksResult(bool Success, bool AccessDenied, string? Output, string? Error);
