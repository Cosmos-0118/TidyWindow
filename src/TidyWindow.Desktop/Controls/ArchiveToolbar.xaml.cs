using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TidyWindow.Next.Desktop;

/// <summary>The search, filters and sort above a long list. Its data context is the list's <see cref="ArchiveState"/>.</summary>
public sealed partial class ArchiveToolbar : UserControl
{
    public ArchiveToolbar() => InitializeComponent();

    private ArchiveState? State => DataContext as ArchiveState;

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (State is { } state && state.Text != SearchBox.Text) state.Text = SearchBox.Text;
    }

    private void ClearClick(object sender, RoutedEventArgs e) => State?.Clear();
}
