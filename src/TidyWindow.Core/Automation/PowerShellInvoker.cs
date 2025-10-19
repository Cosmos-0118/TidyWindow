using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace TidyWindow.Core.Automation;

/// <summary>
/// Provides asynchronous execution helpers for PowerShell scripts using runspaces.
/// </summary>
public sealed class PowerShellInvoker
{
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

        var initialState = InitialSessionState.CreateDefault();

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
                if (kvp.Value is bool boolValue)
                {
                    if (boolValue)
                    {
                        ps.AddParameter(kvp.Key);
                    }
                }
                else
                {
                    ps.AddParameter(kvp.Key, kvp.Value);
                }
            }
        }

        var output = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<string>();

        using PSDataCollection<PSObject> outputCollection = new();
        outputCollection.DataAdded += (_, args) =>
        {
            if (args.Index >= 0 && args.Index < outputCollection.Count)
            {
                output.Add(outputCollection[args.Index].ToString() ?? string.Empty);
            }
        };

        ps.Streams.Error.DataAdded += (_, args) =>
        {
            if (args.Index >= 0 && args.Index < ps.Streams.Error.Count)
            {
                errors.Add(ps.Streams.Error[args.Index].ToString());
            }
        };

        var asyncResult = ps.BeginInvoke<PSObject, PSObject>(input: null, outputCollection);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => ps.Stop());

        var encounteredRuntimeError = false;

        try
        {
            await Task.Factory.FromAsync(asyncResult, ps.EndInvoke).ConfigureAwait(false);
        }
        catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
        {
            errors.Add("Invocation cancelled by request.");
        }
        catch (RuntimeException ex)
        {
            errors.Add(ex.ToString());
            encounteredRuntimeError = true;
        }

        return new PowerShellInvocationResult(
            new ReadOnlyCollection<string>(output.ToList()),
            new ReadOnlyCollection<string>(errors.ToList()),
            ps.HadErrors || encounteredRuntimeError ? 1 : 0);
    }
}

public sealed record PowerShellInvocationResult(
    IReadOnlyList<string> Output,
    IReadOnlyList<string> Errors,
    int ExitCode)
{
    public bool IsSuccess => ExitCode == 0;
}
