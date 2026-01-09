using System;
using System.IO;
using System.Windows.Data;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.App.Views;
using TidyWindow.Core.Startup;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class StartupControllerPageTests
{
    [Fact]
    public async Task OnEntriesFilter_RespectsStartupSourceFlags()
    {
        await WpfTestHelper.Run(() =>
        {
            var page = CreatePage();
            SetDefaults(page);
            SetField(page, "_includeRun", false);

            var runEntry = CreateEntry("run-1", StartupItemSourceKind.RunKey);
            var startupEntry = CreateEntry("startup-1", StartupItemSourceKind.StartupFolder);

            Assert.False(ApplyFilter(page, runEntry));
            Assert.True(ApplyFilter(page, startupEntry));
        });
    }

    [Fact]
    public async Task OnEntriesFilter_AppliesQuickFiltersWithOrLogic()
    {
        await WpfTestHelper.Run(() =>
        {
            var page = CreatePage();
            SetDefaults(page);
            SetField(page, "_filterSafe", true);

            var safeEntry = CreateEntry("safe", StartupItemSourceKind.RunKey, signature: StartupSignatureStatus.SignedTrusted, impact: StartupImpact.Low);
            var unsignedEntry = CreateEntry("unsigned", StartupItemSourceKind.RunKey, signature: StartupSignatureStatus.Unsigned, impact: StartupImpact.Low);
            var highImpactEntry = CreateEntry("heavy", StartupItemSourceKind.RunKey, signature: StartupSignatureStatus.SignedTrusted, impact: StartupImpact.High);

            Assert.True(ApplyFilter(page, safeEntry));
            Assert.False(ApplyFilter(page, unsignedEntry));

            SetField(page, "_filterUnsigned", true);
            Assert.True(ApplyFilter(page, unsignedEntry));

            SetField(page, "_filterSafe", false);
            SetField(page, "_filterHighImpact", true);
            Assert.True(ApplyFilter(page, highImpactEntry));
        });
    }

    [Fact]
    public async Task OnEntriesFilter_MatchesSearchAcrossFields()
    {
        await WpfTestHelper.Run(() =>
        {
            var page = CreatePage();
            SetDefaults(page);
            SetField(page, "_search", "acro");

            var nameMatch = CreateEntry("Acrobat Helper", StartupItemSourceKind.RunKey, publisher: "Adobe");
            var locationMatch = CreateEntry("Updater", StartupItemSourceKind.RunKey, entryLocation: "C:\\tools\\acrobat-updater.exe");
            var nonMatch = CreateEntry("Updater", StartupItemSourceKind.RunKey, publisher: "Contoso", entryLocation: "C:\\tools\\contoso.exe");

            Assert.True(ApplyFilter(page, nameMatch));
            Assert.True(ApplyFilter(page, locationMatch));
            Assert.False(ApplyFilter(page, nonMatch));
        });
    }

    [Fact]
    public async Task OnEntriesFilter_SafeFilterRequiresTrustedUserLowImpact()
    {
        await WpfTestHelper.Run(() =>
        {
            var page = CreatePage();
            SetDefaults(page);
            SetField(page, "_filterSafe", true);

            var trustedUserLow = CreateEntry("safe-user", StartupItemSourceKind.RunKey, isEnabled: true, signature: StartupSignatureStatus.SignedTrusted, impact: StartupImpact.Low, userContext: "CurrentUser");
            var signedButNotTrusted = CreateEntry("signed", StartupItemSourceKind.RunKey, isEnabled: true, signature: StartupSignatureStatus.Signed, impact: StartupImpact.Low, userContext: "CurrentUser");
            var machineScope = CreateEntry("machine", StartupItemSourceKind.RunKey, isEnabled: true, signature: StartupSignatureStatus.SignedTrusted, impact: StartupImpact.Low, userContext: "Machine");
            var service = CreateEntry("service", StartupItemSourceKind.Service, isEnabled: true, signature: StartupSignatureStatus.SignedTrusted, impact: StartupImpact.Low, userContext: "Machine");
            var highImpact = CreateEntry("high", StartupItemSourceKind.RunKey, isEnabled: true, signature: StartupSignatureStatus.SignedTrusted, impact: StartupImpact.High, userContext: "CurrentUser");

            Assert.True(ApplyFilter(page, trustedUserLow));
            Assert.False(ApplyFilter(page, signedButNotTrusted));
            Assert.False(ApplyFilter(page, machineScope));
            Assert.False(ApplyFilter(page, service));
            Assert.False(ApplyFilter(page, highImpact));
        });
    }

    [Fact]
    public void StartupEntryItemViewModel_FormatsLastModifiedDisplay()
    {
        var timestamp = new DateTimeOffset(2024, 12, 31, 23, 45, 0, TimeSpan.Zero);
        var formatted = new StartupEntryItemViewModel(CreateItem("id-1", "Item", timestamp)).LastModifiedDisplay;
        var expected = $"Modified: {timestamp.ToLocalTime():yyyy-MM-dd HH:mm}";
        Assert.Equal(expected, formatted);

        var unknown = new StartupEntryItemViewModel(CreateItem("id-2", "Unknown", null)).LastModifiedDisplay;
        Assert.Equal("Modified: unknown", unknown);
    }

    [Fact]
    public async System.Threading.Tasks.Task SetGuardAsync_PersistsFlagWithoutDisablingWhenAlreadyDisabled()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var guardService = new StartupGuardService(temp);
            var viewModel = new StartupControllerViewModel(
                new StartupInventoryService(),
                new StartupControlService(),
                new StartupDelayService(),
                new ActivityLogService(),
                guardService);

            var entry = new StartupEntryItemViewModel(CreateItem("guarded-1", "Guarded", DateTimeOffset.UtcNow))
            {
                IsEnabled = false
            };

            await viewModel.SetGuardAsync(entry, enabled: true);

            Assert.True(entry.IsAutoGuardEnabled);
            Assert.True(guardService.IsGuarded(entry.Item.Id));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static StartupControllerPage CreatePage()
    {
        return new StartupControllerPage(skipInitializeComponent: true);
    }

    private static bool ApplyFilter(StartupControllerPage page, StartupEntryItemViewModel entry)
    {
        var args = (FilterEventArgs)Activator.CreateInstance(
            typeof(FilterEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { entry },
            culture: null)!;
        var method = typeof(StartupControllerPage).GetMethod("OnEntriesFilter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method!.Invoke(page, new object?[] { page, args });
        return args.Accepted;
    }

    private static void SetDefaults(StartupControllerPage page)
    {
        SetField(page, "_includeRun", true);
        SetField(page, "_includeStartup", true);
        SetField(page, "_includeTasks", true);
        SetField(page, "_includeServices", true);
        SetField(page, "_filterSafe", false);
        SetField(page, "_filterUnsigned", false);
        SetField(page, "_filterHighImpact", false);
        SetField(page, "_showEnabled", true);
        SetField(page, "_showDisabled", true);
        SetField(page, "_search", string.Empty);
    }

    private static void SetField<T>(StartupControllerPage page, string fieldName, T value)
    {
        var field = typeof(StartupControllerPage).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(page, value);
    }

    private static StartupEntryItemViewModel CreateEntry(string id, StartupItemSourceKind kind, bool isEnabled = true, StartupSignatureStatus signature = StartupSignatureStatus.SignedTrusted, StartupImpact impact = StartupImpact.Medium, string publisher = "Publisher", string entryLocation = "HKCU\\Run", string userContext = "CurrentUser")
    {
        var item = CreateItem(id, id, DateTimeOffset.UtcNow, kind, isEnabled, signature, impact, publisher, entryLocation, userContext);
        return new StartupEntryItemViewModel(item);
    }

    private static StartupItem CreateItem(string id, string name, DateTimeOffset? lastModifiedUtc = null, StartupItemSourceKind kind = StartupItemSourceKind.RunKey, bool isEnabled = true, StartupSignatureStatus signature = StartupSignatureStatus.SignedTrusted, StartupImpact impact = StartupImpact.Medium, string publisher = "Publisher", string entryLocation = "HKCU\\Run", string userContext = "CurrentUser")
    {
        return new StartupItem(
            id,
            name,
            "C:\\tools\\app.exe",
            kind,
            "tag",
            null,
            null,
            isEnabled,
            entryLocation,
            publisher,
            signature,
            impact,
            1,
            lastModifiedUtc,
            userContext);
    }
}
