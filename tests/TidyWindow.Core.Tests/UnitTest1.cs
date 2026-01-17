using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TidyWindow.Core.Automation;
using Xunit;

namespace TidyWindow.Core.Tests;

public sealed class PowerShellInvokerTests
{
    [Fact]
    public async Task InvokeScriptAsync_ReturnsOutput()
    {
        string scriptPath = CreateTempScript("param($Name)\n\"Hello $Name\"");
        try
        {
            var invoker = new PowerShellInvoker();
            var result = await invoker.InvokeScriptAsync(scriptPath, new Dictionary<string, object?>
            {
                ["Name"] = "World"
            });

            Assert.True(result.IsSuccess);
            Assert.Contains(result.Output, line => line.Contains("Hello", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(result.Errors);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task InvokeScriptAsync_CapturesErrors()
    {
        string scriptPath = CreateTempScript("throw 'boom'");
        try
        {
            var invoker = new PowerShellInvoker();
            var result = await invoker.InvokeScriptAsync(scriptPath);

            Assert.False(result.IsSuccess);
            Assert.NotEmpty(result.Errors);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task InvokeScriptAsync_AllowsConcurrentInvocations()
    {
        string scriptPath = CreateTempScript("param([int]$Value)\n$Value");
        try
        {
            var invoker = new PowerShellInvoker();

            var tasks = Enumerable.Range(0, 12)
                .Select(i => invoker.InvokeScriptAsync(scriptPath, new Dictionary<string, object?> { ["Value"] = i }));

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.True(r.IsSuccess, string.Join(";", r.Errors)));

            var returnedValues = results
                .SelectMany(r => r.Output)
                .Select(line => line.Trim().Trim('"'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => int.Parse(line, CultureInfo.InvariantCulture))
                .OrderBy(n => n)
                .ToArray();

            Assert.Equal(Enumerable.Range(0, 12), returnedValues);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static string CreateTempScript(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"TidyWindow_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, content);
        return path;
    }
}