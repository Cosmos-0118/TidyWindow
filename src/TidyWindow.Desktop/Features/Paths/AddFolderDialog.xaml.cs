using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TidyWindow.Next.Desktop;

public sealed partial class AddFolderDialog : ContentDialog
{
    private readonly AddFolderViewModel model;
    private readonly Func<Task<string?>> browse;

    /// <summary>The folder the person settled on, from the list or from File Explorer; null if they cancelled.</summary>
    public string? Chosen { get; private set; }

    public AddFolderDialog(AddFolderViewModel model, Func<Task<string?>> browse)
    {
        this.model = model;
        this.browse = browse;
        InitializeComponent();
        DataContext = model;
        Opened += async (_, _) => await model.ScanAsync();
    }

    private void QueryChanged(object sender, TextChangedEventArgs e) => model.Query = ((TextBox)sender).Text;

    private void PrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) => Chosen = model.Selected?.Folder;

    private void ResultDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (model.Selected is not { } row) return;
        Chosen = row.Folder;
        Hide();
    }

    private async void BrowseClick(object sender, RoutedEventArgs e)
    {
        var folder = await browse();
        if (string.IsNullOrWhiteSpace(folder)) return;
        Chosen = folder;
        Hide();
    }
}
