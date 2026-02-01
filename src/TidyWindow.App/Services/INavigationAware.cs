namespace TidyWindow.App.Services;

/// <summary>
/// Interface for pages that need to respond to navigation lifecycle events.
/// Implement this interface on cached pages to properly reset state when navigating
/// to/from the page.
/// </summary>
public interface INavigationAware
{
    /// <summary>
    /// Called when the page is navigated to (becomes the active page).
    /// Use this to reset scroll positions, re-subscribe to events, or refresh stale UI.
    /// </summary>
    void OnNavigatedTo();

    /// <summary>
    /// Called when navigating away from the page.
    /// Use this to clean up temporary state, pause background operations, or clear cached references.
    /// </summary>
    void OnNavigatingFrom();
}
