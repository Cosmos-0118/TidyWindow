using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TidyWindow.Core.Uninstall;

public enum UninstallCommandKind
{
    Msiexec,
    QuietUninstallString,
    UninstallString,
    Winget
}

public sealed class UninstallCommandPlan
{
    public UninstallCommandPlan(
        UninstallCommandKind kind,
        string command,
        IEnumerable<string>? arguments = null,
        bool requiresElevation = true,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command must be provided.", nameof(command));
        }

        Kind = kind;
        Command = command.Trim();
        RequiresElevation = requiresElevation;

        var argumentList = arguments?.Where(static arg => !string.IsNullOrWhiteSpace(arg)).Select(static arg => arg.Trim())
            ?? Enumerable.Empty<string>();
        Arguments = new ReadOnlyCollection<string>(argumentList.ToList());

        Description = string.IsNullOrWhiteSpace(description)
            ? BuildDefaultDescription(kind, Command, Arguments)
            : description.Trim();
    }

    public UninstallCommandKind Kind { get; }

    public string Command { get; }

    public IReadOnlyList<string> Arguments { get; }

    public bool RequiresElevation { get; }

    public string Description { get; }

    public string CommandLine => Arguments.Count == 0
        ? Command
        : Command + " " + string.Join(' ', Arguments);

    public static UninstallCommandPlan ForMsiexec(Guid productCode, bool quiet = true, bool suppressRestart = true)
    {
        var tokens = new List<string> { "/x", productCode.ToString("B") };
        if (quiet)
        {
            tokens.Add("/qn");
        }

        if (suppressRestart)
        {
            tokens.Add("/norestart");
        }

        return new UninstallCommandPlan(UninstallCommandKind.Msiexec, "msiexec.exe", tokens, requiresElevation: true, description: "MSI uninstall");
    }

    public static UninstallCommandPlan ForQuietUninstall(string commandLine, bool requiresElevation)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Command line must be provided.", nameof(commandLine));
        }

        var tokens = Tokenize(commandLine);
        return new UninstallCommandPlan(UninstallCommandKind.QuietUninstallString, tokens.command, tokens.arguments, requiresElevation);
    }

    public static UninstallCommandPlan ForStandardUninstall(string commandLine, bool requiresElevation)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Command line must be provided.", nameof(commandLine));
        }

        var tokens = Tokenize(commandLine);
        return new UninstallCommandPlan(UninstallCommandKind.UninstallString, tokens.command, tokens.arguments, requiresElevation);
    }

    public static UninstallCommandPlan ForWinget(string packageIdentifier, bool silent = true)
    {
        if (string.IsNullOrWhiteSpace(packageIdentifier))
        {
            throw new ArgumentException("Package identifier must be provided.", nameof(packageIdentifier));
        }

        var args = new List<string> { "uninstall", "--id", packageIdentifier.Trim(), "-e" };
        if (silent)
        {
            args.AddRange(new[] { "--silent", "--accept-source-agreements", "--accept-package-agreements" });
        }

        return new UninstallCommandPlan(UninstallCommandKind.Winget, "winget", args, requiresElevation: true, description: "winget fallback");
    }

    private static (string command, IReadOnlyList<string> arguments) Tokenize(string commandLine)
    {
        var reader = new SimpleCommandLineReader(commandLine);
        if (!reader.TryReadToken(out var command))
        {
            throw new ArgumentException("Command line must include an executable.", nameof(commandLine));
        }

        var args = new List<string>();
        while (reader.TryReadToken(out var token))
        {
            args.Add(token);
        }

        return (command, new ReadOnlyCollection<string>(args));
    }

    private static string BuildDefaultDescription(UninstallCommandKind kind, string command, IReadOnlyList<string> arguments)
    {
        return kind switch
        {
            UninstallCommandKind.Msiexec => "MSI uninstall",
            UninstallCommandKind.QuietUninstallString => "Quiet uninstall string",
            UninstallCommandKind.UninstallString => "Uninstall string",
            UninstallCommandKind.Winget => "winget uninstall",
            _ => command + (arguments.Count == 0 ? string.Empty : " " + string.Join(' ', arguments))
        };
    }

    private sealed class SimpleCommandLineReader
    {
        private readonly string _value;
        private int _index;

        public SimpleCommandLineReader(string value)
        {
            _value = value;
            _index = 0;
        }

        public bool TryReadToken(out string token)
        {
            token = string.Empty;
            SkipWhitespace();
            if (_index >= _value.Length)
            {
                return false;
            }

            var inQuotes = false;
            var buffer = new System.Text.StringBuilder();

            while (_index < _value.Length)
            {
                var ch = _value[_index];
                _index++;

                if (ch == '\\')
                {
                    if (_index < _value.Length)
                    {
                        var next = _value[_index];
                        if (next == '"')
                        {
                            buffer.Append('"');
                            _index++;
                            continue;
                        }
                    }

                    buffer.Append('\\');
                    continue;
                }

                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(ch))
                {
                    break;
                }

                buffer.Append(ch);
            }

            token = buffer.ToString();
            return token.Length > 0;
        }

        private void SkipWhitespace()
        {
            while (_index < _value.Length && char.IsWhiteSpace(_value[_index]))
            {
                _index++;
            }
        }
    }
}

public sealed class UninstallOperationPlan
{
    public UninstallOperationPlan(
        string displayName,
        string? publisher,
        string? version,
        bool dryRun,
        IEnumerable<UninstallCommandPlan> steps,
        string? operationId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name must be provided.", nameof(displayName));
        }

        DisplayName = displayName.Trim();
        Publisher = string.IsNullOrWhiteSpace(publisher) ? string.Empty : publisher.Trim();
        Version = string.IsNullOrWhiteSpace(version) ? string.Empty : version.Trim();
        DryRun = dryRun;
        OperationId = string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString("N") : operationId.Trim();

        var normalizedSteps = steps?.ToList() ?? throw new ArgumentNullException(nameof(steps));
        if (normalizedSteps.Count == 0)
        {
            throw new ArgumentException("At least one uninstall step must be provided.", nameof(steps));
        }

        EnsureGuardrails(normalizedSteps);
        Steps = new ReadOnlyCollection<UninstallCommandPlan>(normalizedSteps);

        var normalizedMetadata = metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : metadata.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        Metadata = new ReadOnlyDictionary<string, string>(normalizedMetadata);
    }

    public string OperationId { get; }

    public string DisplayName { get; }

    public string Publisher { get; }

    public string Version { get; }

    public bool DryRun { get; }

    public IReadOnlyList<UninstallCommandPlan> Steps { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public bool IncludesWingetFallback => Steps.Any(static step => step.Kind == UninstallCommandKind.Winget);

    private static void EnsureGuardrails(IReadOnlyList<UninstallCommandPlan> steps)
    {
        foreach (var step in steps)
        {
            if (!Enum.IsDefined(typeof(UninstallCommandKind), step.Kind))
            {
                throw new InvalidOperationException("Unsupported uninstall command kind detected.");
            }

            if (string.Equals(step.Command, "cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("cmd.exe based uninstall steps are not allowed for safety reasons.");
            }

            if (string.Equals(step.Command, "powershell.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(step.Command, "pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Inline PowerShell uninstall steps are not permitted during guardrail phase.");
            }
        }
    }
}
