namespace TidyWindow.App.Services;

/// <summary>
/// Presents confirmation prompts before launching high-impact operations.
/// </summary>
public interface IUserConfirmationService
{
    bool Confirm(string title, string message);
}
