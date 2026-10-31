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
        if (Failure().IsMatch(NoFailure().Replace(text, ""))) return OutputTone.Error;
        if (Warning().IsMatch(text)) return OutputTone.Warning;
        if (kind == CommandOutputKind.Error) return OutputTone.Error;
        if (Success().IsMatch(text)) return OutputTone.Success;
        if (Step().IsMatch(text)) return OutputTone.Progress;
        return kind is CommandOutputKind.Information or CommandOutputKind.Notice ? OutputTone.Information : OutputTone.Normal;
    }
    [GeneratedRegex(@"\b(error|failed|failure|fatal|exception|access denied|permission denied|could not|couldn.t)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Failure();
    [GeneratedRegex(@"\b(no errors?|0 errors?|without errors?|no failures?|0 failures?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoFailure();
    [GeneratedRegex(@"\b(warning|warn|restart required|reboot required|deprecated|checksum.*skip)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Warning();
    [GeneratedRegex(@"\bsuccess(?:ful(?:ly)?)?\b|\b(?:installed|updated|uninstalled|completed) successfully\b|^(?:done|complete|verified|already installed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Success();
    [GeneratedRegex(@"^\s*(?:downloading|extracting|installing|updating|uninstalling|checking|verifying|linking|creating|adding|setting|removing)\b|\b\d{1,3}(?:\.\d+)?%", RegexOptions.IgnoreCase)]
    private static partial Regex Step();
}
