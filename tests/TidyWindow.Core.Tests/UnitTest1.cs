using System;
using System.Collections.Generic;
using System.IO;
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

    private static string CreateTempScript(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"TidyWindow_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, content);
        return path;
    }
}