using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TidyWindow.Core.Automation;

/// <summary>
/// Provides asynchronous execution helpers for PowerShell scripts using runspaces.
/// </summary>
public sealed class PowerShellInvoker
{
    private const int MaxDetailDepth = 4;
    private const int MaxSerializedLength = 4096;

    private static readonly JsonSerializerOptions OutputSerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<PowerShellInvocationResult> InvokeScriptAsync(
        string scriptPath,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            throw new ArgumentException("Script path must be provided.", nameof(scriptPath));
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("The specified PowerShell script was not found.", scriptPath);
        }

        // Use the PS Core default session so core modules like Microsoft.PowerShell.Utility load correctly.
        var initialState = InitialSessionState.CreateDefault2();

        // ImportPSCoreModules exists on PS 6+ and pulls in the intrinsic modules packaged with PowerShell.
        var importProperty = typeof(InitialSessionState).GetProperty("ImportPSCoreModules");
        if (importProperty?.CanWrite == true)
        {
            importProperty.SetValue(initialState, true);
        }

        var executionPolicyProperty = typeof(InitialSessionState).GetProperty("ExecutionPolicy");
        if (executionPolicyProperty?.CanWrite == true)
        {
            var bypassValue = Enum.Parse(executionPolicyProperty.PropertyType, "Bypass");
            executionPolicyProperty.SetValue(initialState, bypassValue);
        }

        using var runspace = RunspaceFactory.CreateRunspace(initialState);
        runspace.Open();

        var scriptDirectory = Path.GetDirectoryName(scriptPath);
        if (!string.IsNullOrEmpty(scriptDirectory))
        {
            runspace.SessionStateProxy.Path.SetLocation(scriptDirectory);
        }

        using PowerShell ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand(scriptPath, useLocalScope: false);

        if (parameters is not null)
        {
            foreach (var kvp in parameters)
            {
                // For the in-process runspace we can add parameters directly; let the PowerShell host
                // handle proper conversion of booleans, arrays, etc.
                ps.AddParameter(kvp.Key, kvp.Value);
            }
        }

        var output = new List<string>();
        var errors = new List<string>();
        var cancellationRequested = false;

        using PSDataCollection<PSObject> outputCollection = new();
        outputCollection.DataAdded += (_, args) =>
        {
            if (args.Index < 0 || args.Index >= outputCollection.Count)
            {
                return;
            }

            var formatted = FormatOutputValue(outputCollection[args.Index]);
            if (string.IsNullOrWhiteSpace(formatted))
            {
                return;
            }

            lock (output)
            {
                output.Add(formatted);
            }
        };

        ps.Streams.Error.DataAdded += (_, args) =>
        {
            if (args.Index < 0 || args.Index >= ps.Streams.Error.Count)
            {
                return;
            }

            var errorRecord = ps.Streams.Error[args.Index];
            foreach (var line in FormatErrorRecord(errorRecord))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lock (errors)
                    {
                        errors.Add(line);
                    }
                }
            }
        };

        var asyncResult = ps.BeginInvoke<PSObject, PSObject>(input: null, outputCollection);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            cancellationRequested = true;
            try
            {
                ps.Stop();
            }
            catch
            {
                // Ignore Stop failures when the pipeline has already completed.
            }
        });

        var encounteredRuntimeError = false;
        var pipelineStoppedForCancellation = false;

        try
        {
            await Task.Factory.FromAsync(asyncResult, ps.EndInvoke).ConfigureAwait(false);
        }
        catch (PipelineStoppedException)
        {
            if (cancellationRequested || cancellationToken.IsCancellationRequested)
            {
                pipelineStoppedForCancellation = true;
            }
            else
            {
                throw;
            }
        }
        catch (RuntimeException ex)
        {
            if (IsMissingBuiltInModuleError(ex))
            {
                return await RunScriptUsingExternalPwshAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
            }

            lock (errors)
            {
                errors.Add(ex.ToString());
            }
            encounteredRuntimeError = true;
        }

        // If the runspace failed due to missing built-in modules (common when hosting on Core without $PSHOME),
        // fall back to launching an external PowerShell process which has the full environment.
        List<string> outputSnapshot;
        List<string> errorSnapshot;
        lock (output)
        {
            outputSnapshot = output.ToList();
        }

        lock (errors)
        {
            errorSnapshot = errors.ToList();
        }

        if (errorSnapshot.Any(IsMissingBuiltInModuleMessage))
        {
            try
            {
                return await RunScriptUsingExternalPwshAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex2)
            {
                errorSnapshot.Add(ex2.ToString());
                return new PowerShellInvocationResult(new ReadOnlyCollection<string>(outputSnapshot), new ReadOnlyCollection<string>(errorSnapshot), 1);
            }
        }

        if (pipelineStoppedForCancellation || cancellationRequested || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return new PowerShellInvocationResult(
            new ReadOnlyCollection<string>(outputSnapshot),
            new ReadOnlyCollection<string>(errorSnapshot),
            ps.HadErrors || encounteredRuntimeError ? 1 : 0);
    }

    private static IEnumerable<string> FormatErrorRecord(ErrorRecord? record)
    {
        if (record is null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in EnumerateErrorMessages(record))
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            var trimmed = message.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                yield return trimmed;
            }
        }
    }

    private static IEnumerable<string> EnumerateErrorMessages(ErrorRecord record)
    {
        var candidates = new List<string?>
        {
            record.Exception?.Message,
            record.ErrorDetails?.Message,
            record.CategoryInfo.Reason,
            record.CategoryInfo.Activity
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            foreach (var line in SplitLines(candidate))
            {
                yield return line;
            }
        }

        if (!string.IsNullOrWhiteSpace(record.TargetObject?.ToString()))
        {
            foreach (var line in SplitLines(record.TargetObject.ToString()!))
            {
                yield return line;
            }
        }

        if (!string.IsNullOrWhiteSpace(record.FullyQualifiedErrorId))
        {
            yield return record.FullyQualifiedErrorId; // useful identifier, typically short.
        }

        var fallback = record.ToString();
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var firstLine = SplitLines(fallback).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                yield return firstLine;
            }
        }
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        return value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsMissingBuiltInModuleError(Exception exception)
    {
        if (exception is null)
        {
            return false;
        }

        if (IsMissingBuiltInModuleMessage(exception.Message))
        {
            return true;
        }

        if (exception.InnerException is not null && IsMissingBuiltInModuleError(exception.InnerException))
        {
            return true;
        }

        return false;
    }

    private static bool IsMissingBuiltInModuleMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.IndexOf("Cannot find the built-in module", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("command was found in the module", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("The 'Select-Object' command was found", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("The 'Split-Path' command was found", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static async Task<PowerShellInvocationResult> RunScriptUsingExternalPwshAsync(string scriptPath, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        // Build argument list: -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "scriptPath" --param1 value1 --flag
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath };

        if (parameters is not null)
        {
            foreach (var kvp in parameters)
            {
                var key = kvp.Key;
                var value = kvp.Value;
                if (value is null)
                {
                    continue;
                }

                if (value is bool b)
                {
                    // Prefer explicit PowerShell boolean literal syntax so parsing is unambiguous
                    // for external pwsh.exe invocation (e.g. -SupportsCustomValue:$true).
                    args.Add(b ? $"-{key}:$true" : $"-{key}:$false");
                }
                else if (value is IEnumerable enumerable && value is not string)
                {
                    // Expand enumerable arguments (for example string[] Buckets) into discrete CLI tokens.
                    var buffered = new List<string>();

                    foreach (var item in enumerable)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        var text = item.ToString();
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }

                        buffered.Add(text.Replace("\"", "\\\""));
                    }

                    if (buffered.Count == 0)
                    {
                        continue;
                    }

                    args.Add($"-{key}");
                    args.AddRange(buffered);
                }
                else
                {
                    var escaped = value.ToString()!.Replace("\"", "\\\"");
                    args.Add($"-{key}");
                    args.Add(escaped);
                }
            }
        }

        var output = new List<string>();
        var errors = new List<string>();

        var pwsh = LocatePowerShellExecutable();
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = pwsh,
            Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? '"' + a + '"' : a)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new System.Diagnostics.Process { StartInfo = startInfo };
        proc.Start();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Suppress kill failures; process may have already exited.
            }
        });

        var outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                output.Add(line);
            }
        });

        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                errors.Add(line);
            }
        });

        try
        {
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var exit = proc.ExitCode;
        return new PowerShellInvocationResult(new ReadOnlyCollection<string>(output), new ReadOnlyCollection<string>(errors), exit);
    }

    private static string LocatePowerShellExecutable()
    {
        try
        {
            var found = System.Environment.GetEnvironmentVariable("PATH")?.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => System.IO.Path.Combine(p, "pwsh.exe"))
                .FirstOrDefault(System.IO.File.Exists);

            if (!string.IsNullOrEmpty(found))
            {
                return found;
            }
        }
        catch
        {
            // ignore
        }

        // Fallback to powershell.exe
        return "powershell.exe";
    }
}

public sealed record PowerShellInvocationResult(
    IReadOnlyList<string> Output,
    IReadOnlyList<string> Errors,
    int ExitCode)
{
    public bool IsSuccess => ExitCode == 0;
}
