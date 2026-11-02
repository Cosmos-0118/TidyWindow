using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum OutputTone { Normal, Information, Progress, Success, Warning, Error }
public sealed record FormattedOutputLine(string Text, OutputTone Tone)
{
    public string Label => Tone switch { OutputTone.Error => "ERROR", OutputTone.Warning => "WARN", OutputTone.Success => "OK", OutputTone.Progress => "STEP", _ => "" };
}
public static partial class CommandOutputFormatter
{
    public static IReadOnlyList<FormattedOutputLine> Format(IEnumerable<CommandOutput> segments)
    {
        var result = new List<FormattedOutputLine>();
        var line = new StringBuilder(); var kind = CommandOutputKind.Output;
        void Flush()
        {
            var text = line.ToString().TrimEnd(); line.Clear();
            if (text.Length > 0) result.Add(new(text, Classify(text, kind)));
        }
        foreach (var segment in segments)
        {
            if (segment.Kind != kind && line.Length > 0) Flush();
            kind = segment.Kind;
            foreach (var c in PackageOutputParser.Clean(segment.Text))
            {
                if (c is '\r' or '\n') Flush();
                else if (!char.IsControl(c) || c == '\t') line.Append(c);
            }
        }
        Flush();
        if (result.Count <= 600) return result;
        return [new("Earlier lines omitted; showing the latest 600 lines.", OutputTone.Information), .. result.TakeLast(600)];
    }
    public static OutputTone Classify(string text, CommandOutputKind kind = CommandOutputKind.Output)
    {
        if (kind == CommandOutputKind.Warning) return OutputTone.Warning;
        if (kind == CommandOutputKind.Progress) return OutputTone.Progress;
        if (kind == CommandOutputKind.Success) return OutputTone.Success;
        try
        {
            if (Failure().IsMatch(NoFailure().Replace(text, ""))) return OutputTone.Error;
            if (Warning().IsMatch(text)) return OutputTone.Warning;
            if (kind == CommandOutputKind.Error) return OutputTone.Error;
            if (Success().IsMatch(text)) return OutputTone.Success;
            if (Step().IsMatch(text)) return OutputTone.Progress;
        }
        // Output is only semi-trusted text: a line built to make the patterns crawl is shown plainly rather than holding up the page.
        catch (RegexMatchTimeoutException) { return kind == CommandOutputKind.Error ? OutputTone.Error : OutputTone.Normal; }
        return kind is CommandOutputKind.Information or CommandOutputKind.Notice ? OutputTone.Information : OutputTone.Normal;
    }
    /// <summary>Like <see cref="Classify"/> for a finished step written in plain words: a non-zero exit code ("it returned 1603") is a failure, and the restart codes are a warning.</summary>
    public static OutputTone ClassifyStep(string text)
    {
        var tone = Classify(text);
        if (tone is not (OutputTone.Normal or OutputTone.Information or OutputTone.Progress)) return tone;
        Match exit;
        try { exit = ExitCode().Match(text); }
        catch (RegexMatchTimeoutException) { return tone; }
        if (!exit.Success) return tone;
        return exit.Groups[1].Value is "3010" or "1641" ? OutputTone.Warning : OutputTone.Error;
    }
    // Every pattern has a time limit, and none may look an unbounded distance ahead: a line can be as long as the whole captured output.
    private const int PatternTimeout = 250;
    [GeneratedRegex(@"\b(?:returned|exit code|exited with(?: code)?)\s+(?!0\b)(\d+)\b", RegexOptions.IgnoreCase, PatternTimeout)]
    private static partial Regex ExitCode();
    [GeneratedRegex(@"\b(error|failed|failure|fatal|exception|access denied|permission denied|could not|couldn.t)\b", RegexOptions.IgnoreCase, PatternTimeout)]
    private static partial Regex Failure();
    [GeneratedRegex(@"\b(no errors?|0 errors?|without errors?|no failures?|0 failures?)\b", RegexOptions.IgnoreCase, PatternTimeout)]
    private static partial Regex NoFailure();
    [GeneratedRegex(@"\b(warning|warn|restart required|reboot required|deprecated|checksum.{0,200}skip)\b", RegexOptions.IgnoreCase, PatternTimeout)]
    private static partial Regex Warning();
    [GeneratedRegex(@"\bsuccess(?:ful(?:ly)?)?\b|\b(?:installed|updated|uninstalled|completed) successfully\b|^(?:done|complete|verified|already installed)\b", RegexOptions.IgnoreCase, PatternTimeout)]
    private static partial Regex Success();
    [GeneratedRegex(@"^\s*(?:downloading|extracting|installing|updating|uninstalling|checking|verifying|linking|creating|adding|setting|removing)\b|\b\d{1,3}(?:\.\d+)?%", RegexOptions.IgnoreCase, PatternTimeout)]
    private static partial Regex Step();
}
