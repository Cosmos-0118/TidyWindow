namespace TidyWindow.Core.Uninstall;

public sealed record AppUninstallResult(InstalledApp App, UninstallOperationResult Operation)
{
    public bool IsSuccess => Operation.IsSuccess;

    public bool UsedWingetFallback => Operation.Plan.IncludesWingetFallback;
}
