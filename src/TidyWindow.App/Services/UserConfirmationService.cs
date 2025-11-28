using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace TidyWindow.App.Services;

/// <summary>
/// Wraps <see cref="MessageBox"/> so view models can request confirmation without hard UI dependencies.
/// </summary>
public sealed class UserConfirmationService : IUserConfirmationService
{
    public bool Confirm(string title, string message)
    {
        var caption = string.IsNullOrWhiteSpace(title) ? "TidyWindow" : title.Trim();
        var prompt = string.IsNullOrWhiteSpace(message) ? "Do you want to continue?" : message.Trim();
        var result = MessageBox.Show(prompt, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}
