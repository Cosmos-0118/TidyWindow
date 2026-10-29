using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

internal static class OutputPalette
{
    // One managed notification from the window's existing UISettings listener. Some unpackaged
    // Windows installations reject AccessibilitySettings.HighContrastChanged with 0x80070490.
    public static event Action? Changed;
    public static void Refresh() => Changed?.Invoke();
    public static Brush Brush(OutputTone tone, bool light)
    {
        try
        {
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                return new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
        }
        catch (COMException) { /* Optional accessibility services must not prevent output from rendering. */ }
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
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(nameof(Document), typeof(object), typeof(CommandOutputView), new PropertyMetadata(null, (sender, _) => ((CommandOutputView)sender).Render()));
    public object? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    private bool listening;
    public CommandOutputView()
    {
        Content = text;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ActualThemeChanged += (_, _) => Render();
        Loaded += (_, _) => { if (!listening) { OutputPalette.Changed += Render; listening = true; } Render(); };
        Unloaded += (_, _) => { if (listening) { OutputPalette.Changed -= Render; listening = false; } };
    }
    private void Render()
    {
        text.Blocks.Clear(); text.FontFamily = FontFamily; text.FontSize = FontSize;
        if (Document is not IReadOnlyList<FormattedOutputLine> lines) return;
        var brushes = new Dictionary<OutputTone, Brush>();
        foreach (var line in lines)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4), LineHeight = 18 };
            if (!brushes.TryGetValue(line.Tone, out var brush)) brushes[line.Tone] = brush = OutputPalette.Brush(line.Tone, ActualTheme == ElementTheme.Light);
            if (line.Label.Length > 0) paragraph.Inlines.Add(new Run { Text = line.Label.PadRight(6), Foreground = brush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            paragraph.Inlines.Add(new Run { Text = line.Text, Foreground = brush });
            text.Blocks.Add(paragraph);
        }
    }
}
