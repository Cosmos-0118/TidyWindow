using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.PackageManagers;

public sealed class PackageManagerInstaller
{
    private static readonly Dictionary<string, string> _aliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winget"] = "winget",
        ["windows package manager client"] = "winget",
        ["choco"] = "choco",
        ["chocolatey"] = "choco",
        ["chocolatey cli"] = "choco",
        ["scoop"] = "scoop",
        ["scoop package manager"] = "scoop"
    };

    private readonly PowerShellInvoker _powerShellInvoker;

    public PackageManagerInstaller(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker;
    }

    public async Task<PowerShellInvocationResult> InstallOrRepairAsync(string managerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(managerName))
        {
            throw new ArgumentException("Manager name must be provided.", nameof(managerName));
        }

        var resolvedName = NormalizeManagerName(managerName);
        var scriptPath = ResolveScriptPath(Path.Combine("automation", "scripts", "install-package-manager.ps1"));
        var parameters = new Dictionary<string, object?>
        {
            ["Manager"] = resolvedName
        };

        return await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeManagerName(string managerName)
    {
        var trimmed = managerName.Trim();
        if (trimmed.Length == 0)
        {
            return managerName;
        }

        return _aliasMap.TryGetValue(trimmed, out var mapped) ? mapped : trimmed;
    }

    private static string ResolveScriptPath(string relativePath)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, relativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate automation script at relative path '{relativePath}'.");
    }
}
