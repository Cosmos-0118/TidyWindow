using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Cleanup;
using WindowsClipboard = System.Windows.Clipboard;

namespace TidyWindow.App.ViewModels;

/// <summary>
/// Celebration phase logic – headline, details, sharing, report, errors popup.
/// </summary>
public sealed partial class CleanupViewModel
{
    private async Task ShowCleanupCelebrationAsync(
        CleanupDeletionResult deletionResult,
        IReadOnlyCollection<string> categoriesTouched,
        IReadOnlyList<CleanupCelebrationFailureViewModel> failureItems,
        TimeSpan executionDuration,
        string? reportPath,
        string deletionSummary)
    {
        var reclaimedMegabytes = deletionResult.TotalBytesDeleted / 1_048_576d;
        var pendingRebootMegabytes = deletionResult.TotalBytesPendingReboot / 1_048_576d;
        CelebrationItemsDeleted = deletionResult.DeletedCount;
        CelebrationItemsSkipped = deletionResult.SkippedCount;
        CelebrationItemsFailed = deletionResult.FailedCount;
        CelebrationItemsPendingReboot = deletionResult.PendingRebootCount;
        CelebrationPendingRebootMegabytes = pendingRebootMegabytes;
        CelebrationReclaimedMegabytes = reclaimedMegabytes;
        var normalizedCategories = NormalizeDistinctCategories(categoriesTouched);
        CelebrationCategoryCount = normalizedCategories.Count;
        UpdateCategoryCollection(_celebrationCategories, normalizedCategories);
        CelebrationCategoryList = BuildCategoryListText(normalizedCategories);

        CelebrationHeadline = BuildCelebrationHeadline(reclaimedMegabytes, pendingRebootMegabytes, deletionResult.DeletedCount, deletionResult.PendingRebootCount);

        CelebrationDetails = BuildCelebrationDetails(deletionResult, CelebrationCategoryCount, reclaimedMegabytes);
        CelebrationDurationDisplay = FormatDuration(executionDuration);
        var estimatedTimeSaved = EstimateTimeSaved(deletionResult.DeletedCount, deletionResult.TotalBytesDeleted, executionDuration);
        CelebrationTimeSavedDisplay = FormatDuration(estimatedTimeSaved);

        CelebrationFailures.Clear();
        IsCelebrationErrorsPopupOpen = false;
        foreach (var failure in failureItems)
        {
            CelebrationFailures.Add(failure);
        }

        CelebrationReportPath = !string.IsNullOrWhiteSpace(reportPath) && File.Exists(reportPath)
            ? reportPath
            : null;

        var shareCategories = CelebrationCategories.Count == 0
            ? CelebrationCategoryListDisplay
            : string.Join(", ", CelebrationCategories);
        CelebrationShareSummary = BuildCelebrationShareSummary(
            CelebrationHeadline,
            deletionResult,
            CelebrationCategoryCount,
            shareCategories,
            CelebrationTimeSavedDisplay,
            CelebrationReportPath);

        if (deletionResult.DeletedCount == 0 && deletionResult.TotalBytesDeleted == 0 && failureItems.Count > 0)
        {
            DeletionStatusMessage = deletionSummary;
        }

        if (IsConfirmationSheetVisible)
        {
            IsConfirmationSheetVisible = false;
        }

        await TransitionToPhaseAsync(
            CleanupPhase.Celebration,
            transitionMessage: "Finalizing cleanup summary…",
            preTransitionDelay: TimeSpan.FromMilliseconds(140),
            settleDelay: TimeSpan.FromMilliseconds(260));
    }

    private static string BuildCelebrationHeadline(double reclaimedMegabytes, double pendingRebootMegabytes, int deletedCount, int pendingRebootCount)
    {
        var hasImmediateReclaim = reclaimedMegabytes > 0.01;
        var hasPendingReclaim = pendingRebootMegabytes > 0.01 && pendingRebootCount > 0;

        if (hasImmediateReclaim && hasPendingReclaim)
        {
            return $"Cleanup complete — {FormatSize(reclaimedMegabytes)} reclaimed ({FormatSize(pendingRebootMegabytes)} after restart)";
        }

        if (hasImmediateReclaim)
        {
            return $"Cleanup complete — {FormatSize(reclaimedMegabytes)} reclaimed";
        }

        if (hasPendingReclaim)
        {
            return $"Cleanup complete — {FormatSize(pendingRebootMegabytes)} will free after restart";
        }

        if (deletedCount > 0 || pendingRebootCount > 0)
        {
            return "Cleanup complete";
        }

        return "No items removed";
    }

    private static string BuildCelebrationDetails(CleanupDeletionResult result, int categoryCount, double reclaimedMegabytes)
    {
        var parts = new List<string>();

        if (result.DeletedCount > 0)
        {
            parts.Add($"{result.DeletedCount:N0} item(s) removed");
        }

        if (result.PendingRebootCount > 0)
        {
            var pendingMb = result.TotalBytesPendingReboot / 1_048_576d;
            if (pendingMb > 0.01)
            {
                parts.Add($"{result.PendingRebootCount:N0} item(s) pending restart ({FormatSize(pendingMb)})");
            }
            else
            {
                parts.Add($"{result.PendingRebootCount:N0} item(s) pending restart");
            }
        }

        if (categoryCount > 0)
        {
            parts.Add($"{categoryCount:N0} categories affected");
        }

        if (reclaimedMegabytes > 0)
        {
            parts.Add($"{FormatSize(reclaimedMegabytes)} reclaimed");
        }

        if (result.SkippedCount > 0)
        {
            parts.Add($"{result.SkippedCount:N0} skipped");
        }

        if (result.FailedCount > 0)
        {
            parts.Add($"{result.FailedCount:N0} failed");
        }

        return parts.Count == 0 ? "No files required cleanup." : string.Join(" • ", parts);
    }

    private static string BuildCelebrationShareSummary(
        string headline,
        CleanupDeletionResult result,
        int categoryCount,
        string categoriesDisplay,
        string timeSavedDisplay,
        string? reportPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine(headline);
        builder.AppendLine($"Items removed: {result.DeletedCount:N0}");
        builder.AppendLine($"Space reclaimed: {FormatSize(result.TotalBytesDeleted / 1_048_576d)}");

        if (result.PendingRebootCount > 0)
        {
            var pendingMb = result.TotalBytesPendingReboot / 1_048_576d;
            builder.AppendLine($"Pending restart: {result.PendingRebootCount:N0} item(s) ({FormatSize(pendingMb)})");
        }

        if (categoryCount > 0 && !string.IsNullOrWhiteSpace(categoriesDisplay) && categoriesDisplay != "—")
        {
            builder.AppendLine($"Categories touched: {categoriesDisplay}");
        }

        builder.AppendLine($"Estimated time saved: {timeSavedDisplay}");

        if (result.FailedCount > 0)
        {
            builder.AppendLine($"Attention needed: {result.FailedCount:N0} item(s) require follow-up.");
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            builder.AppendLine($"Detailed report: {reportPath}");
        }

        return builder.ToString();
    }

    private static TimeSpan EstimateTimeSaved(int deletedCount, long totalBytesDeleted, TimeSpan executionDuration)
    {
        if (deletedCount <= 0 && totalBytesDeleted <= 0)
        {
            return executionDuration;
        }

        var perItemSeconds = Math.Max(0, deletedCount) * 6d;
        var sizeBonusSeconds = Math.Max(0d, totalBytesDeleted / 1_073_741_824d) * 60d;
        var estimateSeconds = Math.Max(30d, perItemSeconds + sizeBonusSeconds);

        var estimate = TimeSpan.FromSeconds(estimateSeconds);
        if (estimate < executionDuration + TimeSpan.FromSeconds(15))
        {
            estimate = executionDuration + TimeSpan.FromSeconds(15);
        }

        return estimate;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:F1} hr";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.TotalMinutes:F1} min";
        }

        return $"{duration.TotalSeconds:F0} sec";
    }

    [RelayCommand]
    private async Task CloseCelebrationAsync()
    {
        CelebrationFailures.Clear();
        var nextPhase = Targets.Count > 0 ? CleanupPhase.Preview : CleanupPhase.Setup;
        var transitionMessage = nextPhase == CleanupPhase.Preview
            ? "Reopening preview…"
            : "Resetting cleanup flow…";

        await TransitionToPhaseAsync(nextPhase, transitionMessage);
        _mainViewModel.SetStatusMessage("Cleanup summary dismissed. Ready for the next scan.");
    }

    [RelayCommand]
    private void ShareCelebrationSummary()
    {
        if (string.IsNullOrWhiteSpace(CelebrationShareSummary))
        {
            _mainViewModel.SetStatusMessage("Cleanup summary is not available yet.");
            return;
        }

        try
        {
            WindowsClipboard.SetText(CelebrationShareSummary);
            _mainViewModel.SetStatusMessage("Cleanup summary copied to clipboard.");
        }
        catch
        {
            _mainViewModel.SetStatusMessage("Unable to access clipboard for sharing.");
        }
    }

    [RelayCommand]
    private void ReviewCelebrationReport()
    {
        var path = CelebrationReportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _mainViewModel.SetStatusMessage("Cleanup report is not available.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
            _mainViewModel.SetStatusMessage("Opening cleanup report...");
        }
        catch (Exception ex)
        {
            _mainViewModel.SetStatusMessage($"Unable to open report: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ShowCelebrationErrorsPopup()
    {
        IsCelebrationErrorsPopupOpen = true;
    }

    [RelayCommand]
    private void HideCelebrationErrorsPopup()
    {
        IsCelebrationErrorsPopupOpen = false;
    }

    partial void OnCelebrationReclaimedMegabytesChanged(double value)
    {
        OnPropertyChanged(nameof(CelebrationReclaimedDisplay));
    }

    partial void OnCelebrationCategoryListChanged(string value)
    {
        OnPropertyChanged(nameof(CelebrationCategoryListDisplay));
    }

    partial void OnCelebrationReportPathChanged(string? value)
    {
        OnPropertyChanged(nameof(CanReviewCelebrationReport));
    }
}
