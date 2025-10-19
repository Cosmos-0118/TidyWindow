using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.PackageManagers;

public sealed class PackageManagerDetector
{
    private readonly PowerShellInvoker _powerShellInvoker;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly PackageManagerDefinition[] _knownManagers =
    {
        new("winget", "Windows Package Manager client", DetectWinget),
        new("choco", "Chocolatey CLI", DetectChocolatey),
        new("scoop", "Scoop package manager", DetectScoop)
    };

    public PackageManagerDetector(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker;
    }

    public async Task<IReadOnlyList<PackageManagerInfo>> DetectAsync(bool includeScoop, bool includeChocolatey, CancellationToken cancellationToken = default)
    {
        var scriptPath = ResolveScriptPath(Path.Combine("automation", "scripts", "bootstrap-package-managers.ps1"));
        var parameters = new Dictionary<string, object?>
        {
            ["IncludeScoop"] = includeScoop,
            ["IncludeChocolatey"] = includeChocolatey
        };

        var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("Package manager detection failed: " + string.Join(Environment.NewLine, result.Errors));
        }

        var jsonPayload = result.Output
            .Reverse()
            .Select(line => line?.Trim())
            .Select(line => line is null ? null : line.TrimStart('\ufeff'))
            .FirstOrDefault(IsLikelyJson);

        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return Array.Empty<PackageManagerInfo>();
        }

        List<PackageManagerJson> models;

        try
        {
            var rootNode = JsonNode.Parse(jsonPayload);
            if (rootNode is JsonArray array)
            {
                models = array
                    .Select(node => node?.Deserialize<PackageManagerJson>(_jsonOptions))
                    .Where(node => node is not null)
                    .Select(node => node!)
                    .ToList();
            }
            else if (rootNode is JsonObject obj)
            {
                var single = obj.Deserialize<PackageManagerJson>(_jsonOptions);
                models = single is not null ? new List<PackageManagerJson> { single } : new List<PackageManagerJson>();
            }
            else
            {
                models = new List<PackageManagerJson>();
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Package manager detection returned invalid JSON.", ex);
        }

        return MergeWithManagedFallbacks(models, includeScoop, includeChocolatey);
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

    private static bool IsLikelyJson(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    private static ImmutableArray<PackageManagerInfo> MergeWithManagedFallbacks(List<PackageManagerJson> models, bool includeScoop, bool includeChocolatey)
    {
        var dictionary = new Dictionary<string, PackageManagerJson>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                continue;
            }

            dictionary[model.Name] = model;
        }

        var results = new List<PackageManagerInfo>();

        foreach (var definition in _knownManagers)
        {
            if (!ShouldInclude(definition.Name, includeScoop, includeChocolatey))
            {
                continue;
            }

            dictionary.TryGetValue(definition.Name, out var model);

            var notes = !string.IsNullOrWhiteSpace(model?.Notes)
                ? model!.Notes!
                : definition.DefaultNotes;

            var isInstalled = model?.Found ?? false;
            if (!isInstalled)
            {
                isInstalled = TryDetect(definition.Detector);
            }

            var name = !string.IsNullOrWhiteSpace(model?.Name) ? model!.Name : definition.Name;

            results.Add(new PackageManagerInfo(name, isInstalled, notes));

            if (model is not null)
            {
                dictionary.Remove(definition.Name);
            }
        }

        foreach (var leftover in dictionary.Values)
        {
            if (leftover is null)
            {
                continue;
            }

            results.Add(new PackageManagerInfo(leftover.Name, leftover.Found, leftover.Notes ?? string.Empty));
        }

        return results.ToImmutableArray();
    }

    private static bool TryDetect(Func<bool> detector)
    {
        try
        {
            return detector();
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldInclude(string name, bool includeScoop, bool includeChocolatey)
    {
        if (string.Equals(name, "scoop", StringComparison.OrdinalIgnoreCase))
        {
            return includeScoop;
        }

        if (string.Equals(name, "choco", StringComparison.OrdinalIgnoreCase))
        {
            return includeChocolatey;
        }

        return true;
    }

    private static bool DetectWinget()
    {
        if (ExecutableExistsInPath("winget"))
        {
            return true;
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.System));
        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

        return ExecutableExists(directories, "winget");
    }

    private static bool DetectChocolatey()
    {
        if (ExecutableExistsInPath("choco"))
        {
            return true;
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var installRoot in EnumerateEnvironmentVariableValues("ChocolateyInstall"))
        {
            AddCandidateDirectory(directories, installRoot, "bin");
            AddCandidateDirectory(directories, installRoot);
        }

        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin");
        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Chocolatey", "bin");
        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Chocolatey", "bin");

        return ExecutableExists(directories, "choco");
    }

    private static bool DetectScoop()
    {
        if (ExecutableExistsInPath("scoop"))
        {
            return true;
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scoopRoot in EnumerateEnvironmentVariableValues("SCOOP"))
        {
            AddCandidateDirectory(directories, scoopRoot, "shims");
        }

        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims");
        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "scoop", "shims");
        AddCandidateDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scoop", "shims");

        if (ExecutableExists(directories, "scoop"))
        {
            return true;
        }

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (File.Exists(Path.Combine(directory, "scoop.ps1")) || File.Exists(Path.Combine(directory, "scoop.cmd")))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExecutableExistsInPath(string executableBaseName) => ExecutableExists(EnumeratePathDirectories(), executableBaseName);

    private static bool ExecutableExists(IEnumerable<string> directories, string executableBaseName)
    {
        var candidateFileNames = BuildCandidateExecutableNames(executableBaseName);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var fileName in candidateFileNames)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, fileName)))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    // Ignore malformed candidate paths.
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> BuildCandidateExecutableNames(string executableBaseName)
    {
        var trimmed = executableBaseName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Array.Empty<string>();
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            trimmed
        };

        var baseNameWithoutExtension = trimmed;
        var extension = Path.GetExtension(trimmed);
        if (!string.IsNullOrEmpty(extension))
        {
            baseNameWithoutExtension = trimmed[..^extension.Length];
        }

        var pathExtValue = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExtValue)
            ? new[] { ".exe", ".cmd", ".bat", ".com" }
            : pathExtValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var extValue in extensions)
        {
            var normalizedExt = extValue.StartsWith('.') ? extValue : "." + extValue;
            candidates.Add(baseNameWithoutExtension + normalizedExt);
        }

        return candidates.ToArray();
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in EnumeratePathSegments())
        {
            AddCandidateDirectory(directories, segment);
        }

        return directories;
    }

    private static IEnumerable<string> EnumeratePathSegments()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in EnumerateEnvironmentVariableValues("PATH"))
        {
            foreach (var segment in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = segment.Trim().Trim('"');
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
    }

    private static IEnumerable<string> EnumerateEnvironmentVariableValues(string variableName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = null;
            try
            {
                value = Environment.GetEnvironmentVariable(variableName, target);
            }
            catch (System.Security.SecurityException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (seen.Add(value))
            {
                yield return value;
            }
        }
    }

    private static void AddCandidateDirectory(HashSet<string> accumulator, params string?[] parts)
    {
        if (accumulator is null || parts is null || parts.Length == 0)
        {
            return;
        }

        string? combined = null;

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                return;
            }

            var trimmed = part.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                return;
            }

            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            combined = combined is null ? expanded : Path.Combine(combined, expanded);
        }

        if (string.IsNullOrWhiteSpace(combined))
        {
            return;
        }

        try
        {
            combined = Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        combined = combined.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (combined.Length == 0)
        {
            return;
        }

        accumulator.Add(combined);
    }

    private sealed record PackageManagerDefinition(string Name, string DefaultNotes, Func<bool> Detector);

    private sealed class PackageManagerJson
    {
        public string Name { get; set; } = string.Empty;
        public bool Found { get; set; }
        public string? Notes { get; set; }
    }
}
