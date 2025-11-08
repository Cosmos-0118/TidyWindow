using System;
using System.Globalization;
using System.Linq;
using System.Resources;

namespace TidyWindow.App.Resources.Strings;

internal static class RegistryOptimizerStrings
{
    private static readonly ResourceManager ResourceManager = new("TidyWindow.App.Resources.Strings.RegistryOptimizer", typeof(RegistryOptimizerStrings).Assembly);

    public static string PageTitle => GetString(nameof(PageTitle), nameof(PageTitle));

    public static string PageHeadline => GetString(nameof(PageHeadline), nameof(PageHeadline));

    public static string LeadCardTitle => GetString(nameof(LeadCardTitle), nameof(LeadCardTitle));

    public static string LeadCardDescription => GetString(nameof(LeadCardDescription), nameof(LeadCardDescription));

    public static string TweaksHeader => GetString(nameof(TweaksHeader), nameof(TweaksHeader));

    public static string TweaksDescription => GetString(nameof(TweaksDescription), nameof(TweaksDescription));

    public static string PresetsHeader => GetString(nameof(PresetsHeader), nameof(PresetsHeader));

    public static string PresetsDescription => GetString(nameof(PresetsDescription), nameof(PresetsDescription));

    public static string ApplyButton => GetString(nameof(ApplyButton), nameof(ApplyButton));

    public static string RevertButton => GetString(nameof(RevertButton), nameof(RevertButton));

    public static string RefreshButton => GetString(nameof(RefreshButton), nameof(RefreshButton));

    public static string PendingNotice => GetString(nameof(PendingNotice), nameof(PendingNotice));

    public static string SyncedNotice => GetString(nameof(SyncedNotice), nameof(SyncedNotice));

    public static string DefaultEnabled => GetString(nameof(DefaultEnabled), nameof(DefaultEnabled));

    public static string DefaultDisabled => GetString(nameof(DefaultDisabled), nameof(DefaultDisabled));

    public static string RiskSafe => GetString(nameof(RiskSafe), nameof(RiskSafe));

    public static string RiskModerate => GetString(nameof(RiskModerate), nameof(RiskModerate));

    public static string RiskCaution => GetString(nameof(RiskCaution), nameof(RiskCaution));

    public static string RiskAdvanced => GetString(nameof(RiskAdvanced), nameof(RiskAdvanced));

    public static string RestoreButton => GetString(nameof(RestoreButton), nameof(RestoreButton));

    public static string RestoreButtonDescription => GetString(nameof(RestoreButtonDescription), nameof(RestoreButtonDescription));

    public static string RollbackDialogTitle => GetString(nameof(RollbackDialogTitle), nameof(RollbackDialogTitle));

    public static string RollbackDialogHeader => GetString(nameof(RollbackDialogHeader), nameof(RollbackDialogHeader));

    public static string RollbackDialogDescription => GetString(nameof(RollbackDialogDescription), nameof(RollbackDialogDescription));

    public static string RollbackDialogKeep => GetString(nameof(RollbackDialogKeep), nameof(RollbackDialogKeep));

    public static string RollbackDialogRevert => GetString(nameof(RollbackDialogRevert), nameof(RollbackDialogRevert));

    public static string RollbackDialogCountdown => GetString(nameof(RollbackDialogCountdown), nameof(RollbackDialogCountdown));

    public static string RollbackDialogAutoNotice => GetString(nameof(RollbackDialogAutoNotice), nameof(RollbackDialogAutoNotice));

    public static string RestorePointCreated => GetString(nameof(RestorePointCreated), nameof(RestorePointCreated));

    public static string RestorePointApplied => GetString(nameof(RestorePointApplied), nameof(RestorePointApplied));

    public static string RestorePointFailed => GetString(nameof(RestorePointFailed), nameof(RestorePointFailed));

    public static string ValueHeaderDetails => GetString(nameof(ValueHeaderDetails), nameof(ValueHeaderDetails));

    public static string ValueHeaderCurrent => GetString(nameof(ValueHeaderCurrent), nameof(ValueHeaderCurrent));

    public static string ValueHeaderRecommended => GetString(nameof(ValueHeaderRecommended), nameof(ValueHeaderRecommended));

    public static string ValueHeaderCustom => GetString(nameof(ValueHeaderCustom), nameof(ValueHeaderCustom));

    public static string ValueNotAvailable => GetString(nameof(ValueNotAvailable), nameof(ValueNotAvailable));

    public static string ValueRecommendationUnavailable => GetString(nameof(ValueRecommendationUnavailable), nameof(ValueRecommendationUnavailable));

    public static string CustomValueNotSupported => GetString(nameof(CustomValueNotSupported), nameof(CustomValueNotSupported));

    public static string CustomValueInfoGeneral => GetString(nameof(CustomValueInfoGeneral), nameof(CustomValueInfoGeneral));

    public static string CustomValueInfoRange => GetString(nameof(CustomValueInfoRange), nameof(CustomValueInfoRange));

    public static string CustomValuePlaceholder => GetString(nameof(CustomValuePlaceholder), nameof(CustomValuePlaceholder));

    public static string GetTweakName(string tweakId, string fallback)
        => GetString(BuildTweakKey(tweakId, "Name"), fallback);

    public static string GetTweakSummary(string tweakId, string fallback)
        => GetString(BuildTweakKey(tweakId, "Summary"), fallback);

    public static string GetTweakRisk(string tweakId, string fallback)
        => GetString(BuildTweakKey(tweakId, "Risk"), fallback);

    private static string GetString(string resourceName, string fallback)
    {
        var value = ResourceManager.GetString(resourceName, CultureInfo.CurrentUICulture);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static string BuildTweakKey(string tweakId, string suffix)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            return suffix;
        }

        var parts = tweakId
            .Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1));

        var core = string.Concat(parts);
        if (core.Length == 0)
        {
            core = "Tweak";
        }

        return $"Tweak_{core}_{suffix}";
    }
}
