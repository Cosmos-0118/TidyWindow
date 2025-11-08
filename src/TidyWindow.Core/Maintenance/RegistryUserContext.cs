namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Provides constants describing environment data shared between elevation layers and registry automation.
/// </summary>
public static class RegistryUserContext
{
    /// <summary>
    /// Command-line argument prefix used to transmit the original interactive user SID when the process restarts with elevation.
    /// </summary>
    public const string OriginalUserSidArgumentPrefix = "--tidyw-original-user-sid=";

    /// <summary>
    /// Environment variable that stores the SID for the original interactive user so automation can target that profile's registry hive.
    /// </summary>
    public const string OriginalUserSidEnvironmentVariable = "TIDYWINDOW_ORIGINAL_USER_SID";
}
