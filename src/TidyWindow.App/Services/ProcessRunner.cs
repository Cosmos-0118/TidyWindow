using System;
using System.Diagnostics;

namespace TidyWindow.App.Services;

public interface IProcessRunner
{
    ProcessRunResult Run(string fileName, string arguments);
}

public readonly record struct ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner : IProcessRunner
{
    public ProcessRunResult Run(string fileName, string arguments)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("A process file name must be provided.", nameof(fileName));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to launch '{fileName}'.");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessRunResult(process.ExitCode, stdOut, stdErr);
    }
}
