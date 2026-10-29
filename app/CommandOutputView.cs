using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

internal static class OutputPalette
{
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    public static Brush Brush(OutputTone tone, bool light)
    {
        if (accessibility.HighContrast)
            return new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
        var color = tone switch
        {
            OutputTone.Success => light ? 0x1C703F : 0xA4E8C1,
            OutputTone.Warning => light ? 0x865500 : 0xF4BF6E,
            OutputTone.Error => light ? 0xB3261E : 0xFF9B93,
            OutputTone.Information or OutputTone.Progress => light ? 0x175CD3 : 0x8AB4FF,
            _ => light ? 0x454545 : 0xC6C6C6
        };
        return new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }
}

public sealed class CommandOutputView : UserControl
{
    private readonly RichTextBlock text = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch };
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(nameof(Document), typeof(object), typeof(CommandOutputView), new PropertyMetadata(null, (sender, _) => ((CommandOutputView)sender).Render()));
    public object? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    private readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    public CommandOutputView()
    {
        Content = text;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ActualThemeChanged += (_, _) => Render();
        Loaded += (_, _) => { accessibility.HighContrastChanged += ContrastChanged; Render(); };
        Unloaded += (_, _) => accessibility.HighContrastChanged -= ContrastChanged;
    }
    private void ContrastChanged(Windows.UI.ViewManagement.AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(Render);
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
