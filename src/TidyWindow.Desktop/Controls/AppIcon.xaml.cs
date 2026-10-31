using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

/// <summary>The square an app's icon sits in. Shows <see cref="Glyph"/> until <see cref="Source"/> has a picture.</summary>
public sealed partial class AppIcon : UserControl
{
    public const string DefaultGlyph = "";

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(AppIcon), new PropertyMetadata(null, (sender, _) => ((AppIcon)sender).Refresh()));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(AppIcon), new PropertyMetadata(DefaultGlyph, (sender, _) => ((AppIcon)sender).Refresh()));
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public AppIcon()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        if (Placeholder is null) return;
        Placeholder.Glyph = string.IsNullOrEmpty(Glyph) ? DefaultGlyph : Glyph;
        Picture.Source = Source;
        var shown = Source is not null;
        Picture.Opacity = shown ? 1 : 0;
        Placeholder.Opacity = shown ? 0 : 1;
    }
}
