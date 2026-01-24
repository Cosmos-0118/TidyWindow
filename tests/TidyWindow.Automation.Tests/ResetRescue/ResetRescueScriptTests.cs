using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Automation.Tests.ResetRescue;

public sealed class ResetRescueScriptTests
{
    private readonly ITestOutputHelper _output;

    public ResetRescueScriptTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Backup_CopiesFiles_ExportsRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = CreateTempRoot();
        var sourceDir = Path.Combine(tempRoot, "source");
        var nested = Path.Combine(sourceDir, "data.txt");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(nested, "payload");
        var targetPath = Path.Combine(tempRoot, "staging");
        var logPath = Path.Combine(tempRoot, "reset-rescue.log");
        var regKey = $"HKEY_CURRENT_USER\\Software\\ResetRescueTest_{Guid.NewGuid():N}";

        try
        {
            CreateRegistryKey(regKey);

            using var result = await RunScriptAsync(
                mode: "Backup",
                sourcePaths: new[] { sourceDir },
                targetPath: targetPath,
                restoreRoot: null,
                conflict: null,
                registryKeys: new[] { regKey },
                logPath: logPath);

            var root = result.Document.RootElement;
            Assert.Equal("Backup", root.GetProperty("mode").GetString());
            Assert.True(string.Equals("ok", root.GetProperty("status").GetString(), StringComparison.OrdinalIgnoreCase), $"json={root.GetRawText()} args={result.Args} stdout={result.Stdout}");

            var copied = root.GetProperty("copied").EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToArray();
            Assert.True(copied.Contains(Path.GetFullPath(sourceDir), StringComparer.OrdinalIgnoreCase), $"copied=[{string.Join(',', copied)}] json={root.GetRawText()} args={result.Args} stdout={result.Stdout}");

            var registryExports = root.GetProperty("registryExports").EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToArray();
            Assert.True(registryExports.Length > 0, $"registryExports empty json={root.GetRawText()} args={result.Args} stdout={result.Stdout}");
            Assert.All(registryExports, export => Assert.True(File.Exists(export), $"missing export {export}"));

            var copiedFile = Path.Combine(targetPath, Path.GetFileName(sourceDir), "data.txt");
            Assert.True(File.Exists(copiedFile), $"missing copied file {copiedFile} json={root.GetRawText()}");
            Assert.Equal("payload", File.ReadAllText(copiedFile));
        }
        finally
        {
            DeleteRegistryKey(regKey);
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task Backup_SkipsMissingPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = CreateTempRoot();
        var missingPath = Path.Combine(tempRoot, "missing");
        var targetPath = Path.Combine(tempRoot, "staging");
        var logPath = Path.Combine(tempRoot, "reset-rescue.log");

        try
        {
            using var result = await RunScriptAsync(
                mode: "Backup",
                sourcePaths: new[] { missingPath },
                targetPath: targetPath,
                restoreRoot: null,
                conflict: null,
                registryKeys: Array.Empty<string>(),
                logPath: logPath);

            var root = result.Document.RootElement;
            Assert.Equal("Backup", root.GetProperty("mode").GetString());
            Assert.True(string.Equals("ok", root.GetProperty("status").GetString(), StringComparison.OrdinalIgnoreCase), $"json={root.GetRawText()} args={result.Args} stdout={result.Stdout}");

            var skipped = root.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToArray();
            Assert.True(skipped.Contains(missingPath, StringComparer.OrdinalIgnoreCase), $"skipped=[{string.Join(',', skipped)}] json={root.GetRawText()} args={result.Args} stdout={result.Stdout}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task Restore_RenameConflict_BacksUpExistingFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = CreateTempRoot();
        var sourceFile = Path.Combine(tempRoot, "payload.txt");
        File.WriteAllText(sourceFile, "new-content");

        var restoreRoot = Path.Combine(tempRoot, "restore");
        Directory.CreateDirectory(restoreRoot);
        var existingPath = Path.Combine(restoreRoot, "payload.txt");
        File.WriteAllText(existingPath, "old-content");

        try
        {
            using var result = await RunScriptAsync(
                mode: "Restore",
                sourcePaths: new[] { sourceFile },
                targetPath: null,
                restoreRoot: restoreRoot,
                conflict: "Rename",
                registryKeys: Array.Empty<string>(),
                logPath: Path.Combine(tempRoot, "restore.log"));

            var root = result.Document.RootElement;
            Assert.Equal("Restore", root.GetProperty("mode").GetString());
            Assert.True(string.Equals("ok", root.GetProperty("status").GetString(), StringComparison.OrdinalIgnoreCase), $"json={root.GetRawText()} args={result.Args} stdout={result.Stdout}");

            var restored = Path.Combine(restoreRoot, "payload.txt");
            var backup = Path.Combine(restoreRoot, "payload.txt-backup");

            Assert.True(File.Exists(restored));
            Assert.True(File.Exists(backup));
            Assert.Equal("new-content", File.ReadAllText(restored));
            Assert.Equal("old-content", File.ReadAllText(backup));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string CreateTempRoot()
    {
        return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ResetRescueScriptTests", Guid.NewGuid().ToString("N"))).FullName;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void CreateRegistryKey(string keyPath)
    {
        var parts = keyPath.Split('\\', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException("Registry path must include hive and subkey", nameof(keyPath));
        }

        var hive = parts[0];
        var subKey = parts[1];
        using var baseKey = hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.Win32.Registry.CurrentUser
            : Microsoft.Win32.Registry.LocalMachine;
        using var created = baseKey.CreateSubKey(subKey);
        created?.SetValue("_ResetRescueTest", "value");
    }

    private static void DeleteRegistryKey(string keyPath)
    {
        var parts = keyPath.Split('\\', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return;
        }

        var hive = parts[0];
        var subKey = parts[1];
        try
        {
            if (hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            }
            else
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // ignore cleanup failure
        }
    }

    private async Task<ScriptResult> RunScriptAsync(
        string mode,
        IReadOnlyList<string> sourcePaths,
        string? targetPath,
        string? restoreRoot,
        string? conflict,
        IReadOnlyList<string> registryKeys,
        string? logPath)
    {
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "automation", "scripts", "reset-rescue.ps1"));

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);

        psi.ArgumentList.Add("-Mode");
        psi.ArgumentList.Add(mode);

        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            psi.ArgumentList.Add("-TargetPath");
            psi.ArgumentList.Add(targetPath);
        }

        if (!string.IsNullOrWhiteSpace(restoreRoot))
        {
            psi.ArgumentList.Add("-RestoreRoot");
            psi.ArgumentList.Add(restoreRoot);
        }

        if (!string.IsNullOrWhiteSpace(conflict))
        {
            psi.ArgumentList.Add("-Conflict");
            psi.ArgumentList.Add(conflict);
        }

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            psi.ArgumentList.Add("-LogPath");
            psi.ArgumentList.Add(logPath);
        }

        if (sourcePaths.Count > 0)
        {
            psi.ArgumentList.Add("-SourcePaths");
            foreach (var source in sourcePaths)
            {
                psi.ArgumentList.Add(source);
            }
        }

        if (registryKeys.Count > 0)
        {
            psi.ArgumentList.Add("-RegistryKeys");
            foreach (var key in registryKeys)
            {
                psi.ArgumentList.Add(key);
            }
        }

        var argsDump = string.Join(' ', psi.ArgumentList);
        _output.WriteLine(argsDump);

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start pwsh process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        _output.WriteLine(stdout);
        _output.WriteLine(stderr);

        Assert.Equal(0, process.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout));

        var jsonLine = stdout.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));

        Assert.False(string.IsNullOrWhiteSpace(jsonLine));

        return new ScriptResult(JsonDocument.Parse(jsonLine!), stdout, argsDump);
    }

    private sealed class ScriptResult : IDisposable
    {
        public ScriptResult(JsonDocument document, string stdout, string args)
        {
            Document = document;
            Stdout = stdout;
            Args = args;
        }

        public JsonDocument Document { get; }
        public string Stdout { get; }
        public string Args { get; }

        public void Dispose()
        {
            Document.Dispose();
        }
    }
}
