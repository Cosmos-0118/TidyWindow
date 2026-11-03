using System.Text;

namespace TidyWindow.Next.Core;

/// <summary>Plain-text forms of crash records, for a support request or a search engine. The Windows account name is replaced, as in the activity log.</summary>
public static class CrashReport
{
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Format(IEnumerable<CrashEvent> events, string? profile = null)
    {
        var text = new StringBuilder();
        foreach (var e in events)
        {
            text.Append($"[{e.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {e.Kind.Label().ToUpperInvariant()}  {e.Headline}");
            if (e.Repeats > 1) text.Append($" (x{e.Repeats})");
            text.AppendLine();
            void Line(string label, string value) { if (value.Length > 0) text.AppendLine($"    {label}: {value}"); }
            Line("App", e.App.Length > 0 && e.Kind.IsApp() ? (e.AppVersion.Length > 0 ? $"{e.App} {e.AppVersion}" : e.App) : "");
            Line("Path", e.AppPath);
            Line("Module", e.Module.Length > 0 ? (e.ModuleVersion.Length > 0 ? $"{e.Module} {e.ModuleVersion}" : e.Module) : "");
            Line("Code", e.Code.Length > 0 ? (e.CodeName.Length > 0 ? $"{e.Code} ({e.CodeName})" : e.Code) : "");
            Line("Offset", e.Offset);
            Line("Process", e.ProcessId);
            Line("Source", $"{e.Source}, event {e.EventId}");
            Line("Report", e.ReportId);
            Line("Dump", e.DumpFile);
            if (e.Detail.Length > 0) foreach (var line in e.Detail.Split('\n')) text.AppendLine("    " + line.TrimEnd());
        }
        return Redact(text.ToString(), profile ?? Profile);
    }

    /// <summary>One program's history in a paragraph: how often, what failed, which versions.</summary>
    public static string Format(CrashApp app, string? profile = null)
    {
        var text = new StringBuilder();
        text.AppendLine($"{app.Name}: {app.Crashes} crash{(app.Crashes == 1 ? "" : "es")}, {app.Hangs} not responding; first {app.First.ToLocalTime():yyyy-MM-dd HH:mm}, last {app.Last.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (app.Path.Length > 0) text.AppendLine($"Path: {app.Path}");
        if (app.Versions.Count > 0) text.AppendLine($"Versions: {string.Join(", ", app.Versions)}");
        foreach (var s in app.Signatures) text.AppendLine($"  {s.Count}x {s.Label}");
        text.AppendLine();
        text.Append(Format(app.Events.Take(20)));
        return Redact(text.ToString(), profile ?? Profile);
    }

    /// <summary>The words to search the web for this problem: the program, the file and the code, which is how other people will have written it up.</summary>
    public static string SearchText(CrashEvent e)
    {
        var parts = e.Kind switch
        {
            CrashKind.BlueScreen => new[] { "blue screen", e.CodeName, e.Code },
            CrashKind.AppHang => new[] { e.FileName, "not responding" },
            CrashKind.AppCrash => new[] { e.FileName, e.Managed.Length > 0 ? e.Managed.Split(':')[0] : e.Module, e.CodeName.Length > 0 ? e.CodeName : e.Code, "crash" },
            _ => new[] { e.App, e.Kind.Label(), e.Headline }
        };
        return string.Join(' ', parts.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string Redact(string text, string profile) => profile.Length > 0 ? text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : text;
}
