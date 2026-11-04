using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// The stack of toasts. Each one is a <see cref="ToastCard"/>, which is read from its own file in the install folder the moment a toast appears.
/// If that folder was changed under the running window (the app was updated or a copy of it removed meanwhile), the file is gone and creating
/// the card throws. From a data template that would end the whole app. Here the card is made in code, so the failure is caught and the message
/// is still shown, in a plain card drawn entirely by code that needs no file.
/// </summary>
public sealed class ToastList : ItemsControl
{
    public ToastList() => DefaultStyleKey = typeof(ItemsControl);

    /// <summary>Told once per failure, so it can be written to the log. The card that could not be made is replaced by a plain one.</summary>
    public Action<Exception>? CardFailed { get; set; }

    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override DependencyObject GetContainerForItemOverride() => new ContentControl { IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        // Not the base: it would put the toast itself in as the content, to be drawn with ToString.
        if (element is not ContentControl holder || item is not ToastItem toast) return;
        holder.Content = Build(toast);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ContentControl holder) holder.Content = null;
    }

    private FrameworkElement Build(ToastItem toast)
    {
        try { return new ToastCard { DataContext = toast }; }
        catch (Exception ex)
        {
            try { CardFailed?.Invoke(ex); } catch (Exception) { /* the log is not worth a second failure */ }
            return Plain(toast);
        }
    }

    /// <summary>The message and a way to close it, with nothing loaded from disk.</summary>
    private static FrameworkElement Plain(ToastItem toast)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = toast.Title, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = true });
        if (toast.Detail.Length > 0) text.Children.Add(new TextBlock { Text = toast.Detail, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Opacity = 0.75, IsTextSelectionEnabled = true });
        var close = new Button { Content = "Dismiss", FontSize = 12, Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, _) => toast.Dismiss();
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        Grid.SetColumn(close, 1); row.Children.Add(close);
        var card = new Border { Padding = new Thickness(14, 12, 10, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = row };
        card.Background = Brush("SolidBackgroundFillColorBaseBrush"); card.BorderBrush = Brush("CardStrokeColorDefaultBrush");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, toast.Announcement);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(card, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return card;
    }

    private static Brush? Brush(string key) => Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
