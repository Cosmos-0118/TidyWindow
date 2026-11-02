using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

internal static class OutputPalette
{
    // One managed notification from the window's existing UISettings listener. Some unpackaged
    // Windows installations reject AccessibilitySettings.HighContrastChanged with 0x80070490.
    public static event Action? Changed;
    public static void Refresh() { SystemUi.Refresh(); Changed?.Invoke(); }
    public static Brush Brush(OutputTone tone, bool light)
    {
        // High contrast: the person's own text colour. Read through the guarded helper, which keeps the answer until the colours change,
        // because this runs for every line of output.
        if (SystemUi.HighContrast) return new SolidColorBrush(SystemUi.Color(Windows.UI.ViewManagement.UIColorType.Foreground, light ? Colors.Black : Colors.White));
        return BrushCache.Rgb(tone switch
        {
            OutputTone.Success => light ? 0x1C703F : 0xA4E8C1,
            OutputTone.Warning => light ? 0x865500 : 0xF4BF6E,
            OutputTone.Error => light ? 0xB3261E : 0xFF9B93,
            OutputTone.Information or OutputTone.Progress => light ? 0x175CD3 : 0x8AB4FF,
            _ => light ? 0x454545 : 0xC6C6C6
        });
    }
}

public sealed class CommandOutputView : UserControl
{
    private readonly RichTextBlock text = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch };
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(nameof(Document), typeof(object), typeof(CommandOutputView), new PropertyMetadata(null, (sender, _) => ((CommandOutputView)sender).Render(fresh: false)));
    public object? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    private bool listening;
    // What the paragraphs show now, and the theme they were coloured for, so an update only touches what changed.
    private IReadOnlyList<FormattedOutputLine> shown = [];
    private bool shownLight;
    // The most lines a document may have dropped from its top for the rest to be kept in place; past that it is rebuilt.
    private const int MaxDrop = 64;

    public CommandOutputView()
    {
        Content = text;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ActualThemeChanged += (_, _) => Render(fresh: true);
        Loaded += (_, _) => { if (!listening) { OutputPalette.Changed += Recolour; listening = true; } Render(fresh: false); };
        Unloaded += (_, _) => { if (listening) { OutputPalette.Changed -= Recolour; listening = false; } };
    }

    private void Recolour() => Render(fresh: true);

    /// <summary>
    /// Brings the paragraphs in line with <see cref="Document"/>. Output grows a few lines at a time, up to ten times a second while a command
    /// runs, so only the difference is touched: new lines go on the end, lines the document dropped from its top come off, and the rest stay
    /// put, with the person's text selection. <paramref name="fresh"/> redraws everything, for a change of theme.
    /// </summary>
    private void Render(bool fresh)
    {
        var lines = Document as IReadOnlyList<FormattedOutputLine> ?? [];
        var light = ActualTheme == ElementTheme.Light;
        text.FontFamily = FontFamily; text.FontSize = FontSize;
        if (fresh || light != shownLight || text.Blocks.Count != shown.Count) { text.Blocks.Clear(); shown = []; }
        var (keep, drop, note) = Overlap(shown, lines);
        for (var i = 0; i < drop; i++) text.Blocks.RemoveAt(keep);
        var brushes = new Dictionary<OutputTone, Brush>();
        Paragraph Make(FormattedOutputLine line)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4), LineHeight = 18 };
            if (!brushes.TryGetValue(line.Tone, out var brush)) brushes[line.Tone] = brush = OutputPalette.Brush(line.Tone, light);
            if (line.Label.Length > 0) paragraph.Inlines.Add(new Run { Text = line.Label.PadRight(6), Foreground = brush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            paragraph.Inlines.Add(new Run { Text = line.Text, Foreground = brush });
            return paragraph;
        }
        if (note) text.Blocks.Insert(keep, Make(lines[keep]));
        // Everything in place now matches the document up to here; the rest is new.
        for (var i = keep + (note ? 1 : 0) + (shown.Count - keep - drop); i < lines.Count; i++) text.Blocks.Add(Make(lines[i]));
        shown = lines; shownLight = light;
    }

    // (keep, drop, note): the first `keep` shown lines are unchanged; the next `drop` are gone from the document; `note` says the document has
    // one new line where they were (its "earlier lines omitted" note) before the remaining shown lines carry on. Whatever the answer, applying
    // it leaves the paragraphs equal to the document: when nothing overlaps, everything after the common start is replaced.
    private static (int Keep, int Drop, bool Note) Overlap(IReadOnlyList<FormattedOutputLine> shown, IReadOnlyList<FormattedOutputLine> lines)
    {
        var keep = 0;
        while (keep < shown.Count && keep < lines.Count && shown[keep] == lines[keep]) keep++;
        if (keep == shown.Count) return (keep, 0, false);
        for (var drop = 1; keep + drop < shown.Count && drop <= MaxDrop; drop++)
        {
            if (Continues(shown, keep + drop, lines, keep)) return (keep, drop, false);
            if (Continues(shown, keep + drop, lines, keep + 1)) return (keep, drop, true);
        }
        return (keep, shown.Count - keep, false);
    }

    // True when every shown line from `from` on appears, in order, in the document from `at` on.
    private static bool Continues(IReadOnlyList<FormattedOutputLine> shown, int from, IReadOnlyList<FormattedOutputLine> lines, int at)
    {
        if (lines.Count - at < shown.Count - from) return false;
        for (var i = 0; from + i < shown.Count; i++) if (shown[from + i] != lines[at + i]) return false;
        return true;
    }
}
