using System.Globalization;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>How far along a running command says it is: "Downloading" for transfers, "Running" for any other step that reports a percentage.</summary>
public sealed record CommandProgress(string Label, int Percent)
{
    public string Text => $"{Label} {Percent}%";
}

public static partial class CommandProgressReader
{
    // Managers redraw one progress line in place with carriage returns, so only the newest line says where things are now.
    private const int Window = 2048;

    [GeneratedRegex(@"(?<![\d.])(\d{1,3})(?:\.\d+)?\s*%")]
    private static partial Regex Percent();
    [GeneratedRegex(@"(?<![\d.])(\d+(?:\.\d+)?)\s*(B|KB|MB|GB)\s*/\s*(\d+(?:\.\d+)?)\s*(B|KB|MB|GB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Size();
    [GeneratedRegex(@"\bdownload", RegexOptions.IgnoreCase)]
    private static partial Regex Download();
    // What is left once the bar, the numbers and the units are taken away: nothing but WinGet's own bar means the line is a transfer.
    [GeneratedRegex(@"[\p{So}\p{Sm}\s\d.,%/\-\\|█▒░=>#\[\]()]|\b(?:B|KB|MB|GB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BarOrNumber();

    /// <summary>The progress on the newest non-empty line of the output, or null when that line does not report any.</summary>
    public static CommandProgress? Latest(string output)
    {
        if (output.Length == 0) return null;
        var tail = PackageOutputParser.Clean(output.Length > Window ? output[^Window..] : output);
        var line = tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrEmpty(line)) return null;
        var percent = Fraction(line);
        if (percent is null) return null;
        var transfer = Download().IsMatch(line) || BarOrNumber().Replace(line, "").Length == 0;
        return new(transfer ? "Downloading" : "Running", percent.Value);
    }

    private static int? Fraction(string line)
    {
        var match = Percent().Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= 100) return value;
        var size = Size().Match(line);
        if (!size.Success) return null;
        var done = Bytes(size.Groups[1].Value, size.Groups[2].Value);
        var total = Bytes(size.Groups[3].Value, size.Groups[4].Value);
        return total > 0 && done <= total ? (int)Math.Floor(done / total * 100) : null;
    }

    private static double Bytes(string number, string unit)
    {
        var value = double.Parse(number, CultureInfo.InvariantCulture);
        return unit.ToUpperInvariant() switch { "KB" => value * 1024, "MB" => value * 1024 * 1024, "GB" => value * 1024 * 1024 * 1024, _ => value };
    }
}
