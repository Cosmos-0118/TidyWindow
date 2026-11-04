using Microsoft.UI.Xaml.Controls;

namespace TidyWindow.Next.Desktop;

internal static class DialogExtensions
{
    /// <summary>Shows the dialog through the window's one-at-a-time gate (see <see cref="MainWindow.ShowDialogAsync"/>); answers None when the window is gone or another dialog is up.</summary>
    public static Task<ContentDialogResult> ShowGatedAsync(this ContentDialog dialog) =>
        MainWindow.Open is { } window ? window.ShowDialogAsync(dialog) : Task.FromResult(ContentDialogResult.None);
}
