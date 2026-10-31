using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TidyWindow.Next.Core;

/// <summary>Reads PowerShell's stderr serialization as messages, never as executable/deserialized objects.</summary>
public sealed partial class PowerShellOutputDecoder(CommandOutputKind fallback)
{
    private const string Marker = "#< CLIXML";
    private readonly StringBuilder pending = new();
    private int mode; // 0: plain text with marker detection, 2: serialized records
    public IReadOnlyList<CommandOutput> Feed(string chunk, bool final = false)
    {
        pending.Append(chunk);
        var result = new List<CommandOutput>();
        while (pending.Length > 0)
        {
            var text = pending.ToString();
            if (mode != 2)
            {
                var marker = text.IndexOf(Marker, StringComparison.Ordinal);
                if (marker >= 0)
                {
                    if (marker > 0) result.Add(new(text[..marker], fallback));
                    pending.Remove(0, marker + Marker.Length); mode = 2; continue;
                }
                var hold = 0;
                if (!final)
                    for (var length = Math.Min(text.Length, Marker.Length - 1); length > 0; length--)
                        if (Marker.StartsWith(text[^length..], StringComparison.Ordinal)) { hold = length; break; }
                if (text.Length > hold) result.Add(new(text[..(text.Length - hold)], fallback));
                pending.Clear(); if (hold > 0) pending.Append(text[^hold..]);
                return result;
            }
            var whitespace = text.Length - text.TrimStart().Length;
            if (whitespace > 0) { pending.Remove(0, whitespace); continue; }
            if (text.StartsWith(Marker, StringComparison.Ordinal)) { pending.Remove(0, Marker.Length); continue; }
            if (text.StartsWith("<Objs", StringComparison.Ordinal))
            { var end = TagEnd(text, 0); if (end < 0) break; pending.Remove(0, end + 1); continue; }
            if (text.StartsWith("</Objs>", StringComparison.Ordinal)) { pending.Remove(0, 7); mode = 0; continue; }
            if (text.StartsWith("<?xml", StringComparison.Ordinal))
            { var end = text.IndexOf("?>", StringComparison.Ordinal); if (end < 0) break; pending.Remove(0, end + 2); continue; }
            if (text[0] != '<')
            {
                var next = text.IndexOf('<');
                if (next < 0 && !final) break;
                next = next < 0 ? text.Length : next;
                result.Add(new(text[..next], fallback)); pending.Remove(0, next); continue;
            }
            var position = 0; var depth = 0; var complete = -1;
            while (position < text.Length)
            {
                var start = text.IndexOf('<', position); if (start < 0) break;
                var end = TagEnd(text, start); if (end < 0) break;
                if (start + 1 < text.Length && text[start + 1] == '/') depth--;
                else if (start + 1 < text.Length && text[start + 1] != '?') depth++;
                if (text[end - 1] == '/') depth--;
                position = end + 1;
                if (depth == 0) { complete = position; break; }
            }
            if (complete < 0) break;
            Decode(text[..complete], result); pending.Remove(0, complete);
        }
        if (pending.Length > 262144 || final && pending.ToString().Trim().Length > 0)
        {
            result.Add(new("PowerShell returned an incomplete or unreadable output record. Internal metadata was omitted.\n", CommandOutputKind.Warning));
            pending.Clear();
        }
        return result;
    }
    private static int TagEnd(string text, int start)
    {
        var quote = '\0';
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }
    private static void Decode(string fragment, List<CommandOutput> result)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(fragment), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 });
            var record = XElement.Load(reader);
            var stream = record.Attribute("S")?.Value.ToLowerInvariant();
            // Write-Host is already emitted as plain stdout. Its PSHOST information copy is metadata.
            if (stream == "information" && record.Descendants().Any(x => x.Name.LocalName == "S" && x.Value == "PSHOST")) return;
            string message;
            var kind = stream switch { "error" => CommandOutputKind.Error, "warning" => CommandOutputKind.Warning, "progress" => CommandOutputKind.Progress, _ => CommandOutputKind.Information };
            if (stream == "progress")
            {
                var progress = record.Descendants().FirstOrDefault(x => x.Name.LocalName == "PR");
                if (progress is null || progress.Elements().Any(x => x.Name.LocalName == "T" && x.Value == "Completed")) return;
                message = string.Join(" · ", progress.Elements().Where(x => x.Name.LocalName is "AV" or "SD").Select(x => x.Value.Trim()).Where(x => x.Length > 0));
                var percent = progress.Elements().FirstOrDefault(x => x.Name.LocalName == "PC")?.Value;
                if (int.TryParse(percent, out var value) && value is >= 0 and <= 100) message += $" · {value}%";
            }
            else message = record.Name.LocalName == "S" ? record.Value : record.Elements().FirstOrDefault(x => x.Name.LocalName == "ToString")?.Value
                ?? record.Descendants().FirstOrDefault(x => x.Attribute("N")?.Value is "Message" or "MessageData")?.Value ?? "";
            message = Escape().Replace(message, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            if (message.Length > 0) result.Add(new(message.EndsWith('\n') ? message : message + "\n", kind));
        }
        catch (XmlException) { result.Add(new("PowerShell returned an unreadable output record. Internal metadata was omitted.\n", CommandOutputKind.Warning)); }
    }
    [GeneratedRegex(@"_x([0-9a-fA-F]{4})_")]
    private static partial Regex Escape();
}
