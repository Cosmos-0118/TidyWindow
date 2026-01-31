using System;
using System.IO;
using System.Linq;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Startup;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class StartupEntryItemViewModelBackupTests
{
    #region Backup Constructor Tests

    [Fact]
    public void Constructor_FromBackup_SetsIsBackupOnlyTrue()
    {
        var backup = CreateRunKeyBackup("backup-id", "TestEntry");

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.True(viewModel.IsBackupOnly);
    }

    [Fact]
    public void Constructor_FromBackup_StoresBackupRecord()
    {
        var backup = CreateRunKeyBackup("backup-id", "TestEntry");

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.NotNull(viewModel.BackupRecord);
        Assert.Equal("backup-id", viewModel.BackupRecord.Id);
    }

    [Fact]
    public void Constructor_FromBackup_CreatesSyntheticItem()
    {
        var backup = CreateRunKeyBackup("backup-id", "TestEntry");

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.NotNull(viewModel.Item);
        Assert.Equal("backup-id", viewModel.Item.Id);
        Assert.Equal("TestEntry", viewModel.Item.Name);
        Assert.Equal(StartupItemSourceKind.RunKey, viewModel.Item.SourceKind);
    }

    [Fact]
    public void Constructor_FromBackup_SetsIsEnabledFalse()
    {
        var backup = CreateRunKeyBackup("backup-id", "TestEntry");

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.False(viewModel.IsEnabled);
        Assert.False(viewModel.Item.IsEnabled);
    }

    [Fact]
    public void Constructor_FromBackup_RunKey_BuildsCorrectEntryLocation()
    {
        var backup = new StartupEntryBackup(
            Id: "run:HKCU Run:TestEntry",
            SourceKind: StartupItemSourceKind.RunKey,
            RegistryRoot: "HKCU",
            RegistrySubKey: "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            RegistryValueName: "TestEntry",
            RegistryValueData: "C:\\test.exe",
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.Equal("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", viewModel.Item.EntryLocation);
    }

    [Fact]
    public void Constructor_FromBackup_StartupFolder_BuildsCorrectEntryLocation()
    {
        var backup = new StartupEntryBackup(
            Id: "startup:HKCU Startup:test.lnk",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: "C:\\Users\\Test\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\test.lnk",
            FileBackupPath: "C:\\ProgramData\\TidyWindow\\StartupBackups\\StartupFiles\\test.lnk",
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.Equal("C:\\Users\\Test\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\test.lnk", viewModel.Item.EntryLocation);
    }

    [Fact]
    public void Constructor_FromBackup_Service_BuildsCorrectEntryLocation()
    {
        var backup = new StartupEntryBackup(
            Id: "svc:TestService",
            SourceKind: StartupItemSourceKind.Service,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: "TestService",
            ServiceStartValue: 2,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.Equal("TestService", viewModel.Item.EntryLocation);
    }

    [Fact]
    public void Constructor_FromBackup_ScheduledTask_BuildsCorrectEntryLocation()
    {
        var backup = new StartupEntryBackup(
            Id: "task:\\Test\\MyTask",
            SourceKind: StartupItemSourceKind.ScheduledTask,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: "\\Test\\MyTask",
            TaskEnabled: true,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.Equal("\\Test\\MyTask", viewModel.Item.EntryLocation);
    }

    [Fact]
    public void Constructor_FromBackup_UsesServiceNameAsDisplayName()
    {
        var backup = new StartupEntryBackup(
            Id: "svc:MyTestService",
            SourceKind: StartupItemSourceKind.Service,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: "MyTestService",
            ServiceStartValue: 2,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.Equal("MyTestService", viewModel.Name);
    }

    [Fact]
    public void Constructor_FromBackup_UsesTaskPathAsDisplayName()
    {
        var backup = new StartupEntryBackup(
            Id: "task:\\MyApp\\UpdateTask",
            SourceKind: StartupItemSourceKind.ScheduledTask,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: "\\MyApp\\UpdateTask",
            TaskEnabled: true,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.Equal("\\MyApp\\UpdateTask", viewModel.Name);
    }

    [Fact]
    public void Constructor_FromBackup_SetsUnknownSignatureStatus()
    {
        var backup = CreateRunKeyBackup("backup-id", "TestEntry");

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.Equal(StartupSignatureStatus.Unknown, viewModel.Item.SignatureStatus);
    }

    [Fact]
    public void Constructor_FromBackup_SetsUnknownImpact()
    {
        var backup = CreateRunKeyBackup("backup-id", "TestEntry");

        var viewModel = new StartupEntryItemViewModel(backup);

        Assert.Equal(StartupImpact.Unknown, viewModel.Item.Impact);
    }

    [Fact]
    public void Constructor_FromBackup_WithNullBackup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StartupEntryItemViewModel((StartupEntryBackup)null!));
    }

    #endregion

    #region Regular Constructor Comparison

    [Fact]
    public void Constructor_FromStartupItem_SetsIsBackupOnlyFalse()
    {
        var item = CreateStartupItem("item-id", "TestItem");

        var viewModel = new StartupEntryItemViewModel(item);

        Assert.False(viewModel.IsBackupOnly);
    }

    [Fact]
    public void Constructor_FromStartupItem_HasNullBackupRecord()
    {
        var item = CreateStartupItem("item-id", "TestItem");

        var viewModel = new StartupEntryItemViewModel(item);

        Assert.Null(viewModel.BackupRecord);
    }

    #endregion

    #region Deduplication Support

    [Fact]
    public void MultipleViewModels_FromSameBackup_HaveSameItemId()
    {
        var backup = CreateRunKeyBackup("shared-id", "SharedEntry");

        var vm1 = new StartupEntryItemViewModel(backup);
        var vm2 = new StartupEntryItemViewModel(backup);

        Assert.Equal(vm1.Item.Id, vm2.Item.Id);
    }

    [Fact]
    public void BackupAndLiveItem_WithSameId_CanBeCompared()
    {
        var backup = CreateRunKeyBackup("same-id", "TestEntry");
        var liveItem = CreateStartupItem("same-id", "TestEntry");

        var backupVm = new StartupEntryItemViewModel(backup);
        var liveVm = new StartupEntryItemViewModel(liveItem);

        Assert.Equal(backupVm.Item.Id, liveVm.Item.Id);
        Assert.True(backupVm.IsBackupOnly);
        Assert.False(liveVm.IsBackupOnly);
    }

    #endregion

    #region Delete Backup Eligibility

    [Fact]
    public void BackupOnlyEntry_CanBeDeleted()
    {
        var backup = CreateRunKeyBackup("backup-id", "TestEntry");
        var viewModel = new StartupEntryItemViewModel(backup);

        // Entry is backup-only and has a backup record
        Assert.True(viewModel.IsBackupOnly);
        Assert.NotNull(viewModel.BackupRecord);
    }

    [Fact]
    public void LiveEntry_CannotBeDeleted()
    {
        var item = CreateStartupItem("live-id", "LiveEntry");
        var viewModel = new StartupEntryItemViewModel(item);

        // Entry is not backup-only
        Assert.False(viewModel.IsBackupOnly);
        Assert.Null(viewModel.BackupRecord);
    }

    [Fact]
    public void BackupEntry_HasCorrectIdForDeletion()
    {
        var backup = CreateRunKeyBackup("delete-me-id", "ToDelete");
        var viewModel = new StartupEntryItemViewModel(backup);

        // The backup record ID should match the item ID
        Assert.Equal("delete-me-id", viewModel.Item.Id);
        Assert.Equal("delete-me-id", viewModel.BackupRecord!.Id);
    }

    #endregion

    #region Helpers

    private static StartupEntryBackup CreateRunKeyBackup(string id, string valueName)
    {
        return new StartupEntryBackup(
            Id: id,
            SourceKind: StartupItemSourceKind.RunKey,
            RegistryRoot: "HKCU",
            RegistrySubKey: "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            RegistryValueName: valueName,
            RegistryValueData: $"C:\\tools\\{valueName}.exe",
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static StartupItem CreateStartupItem(string id, string name)
    {
        return new StartupItem(
            Id: id,
            Name: name,
            ExecutablePath: "C:\\tools\\app.exe",
            SourceKind: StartupItemSourceKind.RunKey,
            SourceTag: "HKCU Run",
            Arguments: null,
            RawCommand: null,
            IsEnabled: true,
            EntryLocation: "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            Publisher: "TestPublisher",
            SignatureStatus: StartupSignatureStatus.SignedTrusted,
            Impact: StartupImpact.Medium,
            FileSizeBytes: 1024,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            UserContext: "CurrentUser");
    }

    #endregion
}
