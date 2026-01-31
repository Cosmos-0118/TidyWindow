using System;
using System.IO;
using TidyWindow.Core.Startup;
using Xunit;

namespace TidyWindow.Core.Tests.Backup;

public sealed class StartupBackupStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly StartupBackupStore _store;

    public StartupBackupStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "TidyWindowTests", "BackupStore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _store = new StartupBackupStore(_tempRoot);
    }

    public void Dispose()
    {
        TryDeleteDirectory(_tempRoot);
    }

    #region Basic Operations

    [Fact]
    public void Save_And_Get_ReturnsCorrectBackup()
    {
        var backup = CreateRunKeyBackup("test-id-1", "TestEntry");

        _store.Save(backup);
        var retrieved = _store.Get("test-id-1");

        Assert.NotNull(retrieved);
        Assert.Equal("test-id-1", retrieved.Id);
        Assert.Equal("TestEntry", retrieved.RegistryValueName);
    }

    [Fact]
    public void Get_WithNonExistentId_ReturnsNull()
    {
        var result = _store.Get("non-existent-id");

        Assert.Null(result);
    }

    [Fact]
    public void Get_WithNullOrEmptyId_ReturnsNull()
    {
        Assert.Null(_store.Get(null!));
        Assert.Null(_store.Get(string.Empty));
        Assert.Null(_store.Get("   "));
    }

    [Fact]
    public void Remove_DeletesBackup()
    {
        var backup = CreateRunKeyBackup("remove-test", "ToRemove");
        _store.Save(backup);

        _store.Remove("remove-test");

        Assert.Null(_store.Get("remove-test"));
    }

    [Fact]
    public void Remove_WithNonExistentId_DoesNotThrow()
    {
        var exception = Record.Exception(() => _store.Remove("non-existent"));
        Assert.Null(exception);
    }

    [Fact]
    public void GetAll_ReturnsAllBackups()
    {
        _store.Save(CreateRunKeyBackup("id-1", "Entry1"));
        _store.Save(CreateRunKeyBackup("id-2", "Entry2"));
        _store.Save(CreateRunKeyBackup("id-3", "Entry3"));

        var all = _store.GetAll();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void Save_WithDuplicateId_OverwritesExisting()
    {
        _store.Save(CreateRunKeyBackup("dup-id", "Original"));
        _store.Save(CreateRunKeyBackup("dup-id", "Updated"));

        var all = _store.GetAll();
        var retrieved = _store.Get("dup-id");

        Assert.Single(all);
        Assert.Equal("Updated", retrieved?.RegistryValueName);
    }

    [Fact]
    public void GetAll_IsCaseInsensitive()
    {
        _store.Save(CreateRunKeyBackup("TEST-ID", "Entry1"));
        _store.Save(CreateRunKeyBackup("test-id", "Entry2")); // Should overwrite

        var all = _store.GetAll();

        Assert.Single(all);
    }

    #endregion

    #region IsValidBackup Tests

    [Fact]
    public void IsValidBackup_WithNull_ReturnsFalse()
    {
        Assert.False(StartupBackupStore.IsValidBackup(null));
    }

    [Fact]
    public void IsValidBackup_WithEmptyId_ReturnsFalse()
    {
        var backup = new StartupEntryBackup(
            Id: "",
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

        Assert.False(StartupBackupStore.IsValidBackup(backup));
    }

    [Fact]
    public void IsValidBackup_WithRegistryValueName_ReturnsTrue()
    {
        var backup = CreateRunKeyBackup("valid-1", "TestEntry");
        Assert.True(StartupBackupStore.IsValidBackup(backup));
    }

    [Fact]
    public void IsValidBackup_WithServiceName_ReturnsTrue()
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

        Assert.True(StartupBackupStore.IsValidBackup(backup));
    }

    [Fact]
    public void IsValidBackup_WithTaskPath_ReturnsTrue()
    {
        var backup = new StartupEntryBackup(
            Id: "task:TestTask",
            SourceKind: StartupItemSourceKind.ScheduledTask,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: "\\Microsoft\\TestTask",
            TaskEnabled: true,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        Assert.True(StartupBackupStore.IsValidBackup(backup));
    }

    [Fact]
    public void IsValidBackup_WithFileOriginalPath_ReturnsTrue()
    {
        var backup = new StartupEntryBackup(
            Id: "startup:Test",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: "C:\\Startup\\test.lnk",
            FileBackupPath: "C:\\Backup\\test.lnk",
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        Assert.True(StartupBackupStore.IsValidBackup(backup));
    }

    [Fact]
    public void IsValidBackup_WithNoIdentifyingFields_ReturnsFalse()
    {
        var backup = new StartupEntryBackup(
            Id: "orphan-id",
            SourceKind: StartupItemSourceKind.RunKey,
            RegistryRoot: "HKCU",
            RegistrySubKey: "Software\\Test",
            RegistryValueName: null, // No value name
            RegistryValueData: null,
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        Assert.False(StartupBackupStore.IsValidBackup(backup));
    }

    #endregion

    #region CleanupStaleBackups Tests

    [Fact]
    public void CleanupStaleBackups_RemovesEntriesWithMissingBackupFile()
    {
        var backupDir = Path.Combine(_tempRoot, "StartupFiles");
        Directory.CreateDirectory(backupDir);

        // Create a backup entry pointing to a non-existent backup file
        var staleBackup = new StartupEntryBackup(
            Id: "startup:stale",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: "C:\\Users\\Test\\Startup\\missing.lnk",
            FileBackupPath: Path.Combine(backupDir, "missing.lnk"), // File doesn't exist
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        _store.Save(staleBackup);
        Assert.Single(_store.GetAll());

        var removed = _store.CleanupStaleBackups();

        Assert.Equal(1, removed);
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void CleanupStaleBackups_KeepsEntriesWithExistingBackupFile()
    {
        var backupDir = Path.Combine(_tempRoot, "StartupFiles");
        Directory.CreateDirectory(backupDir);
        var backupFilePath = Path.Combine(backupDir, "existing.lnk");
        File.WriteAllText(backupFilePath, "dummy");

        var validBackup = new StartupEntryBackup(
            Id: "startup:valid",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: "C:\\Users\\Test\\Startup\\existing.lnk",
            FileBackupPath: backupFilePath, // File exists
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        _store.Save(validBackup);

        var removed = _store.CleanupStaleBackups();

        Assert.Equal(0, removed);
        Assert.Single(_store.GetAll());
    }

    [Fact]
    public void CleanupStaleBackups_KeepsEntriesWhereOriginalFileWasRestored()
    {
        var backupDir = Path.Combine(_tempRoot, "StartupFiles");
        var originalDir = Path.Combine(_tempRoot, "OriginalStartup");
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(originalDir);
        var originalFilePath = Path.Combine(originalDir, "restored.lnk");
        File.WriteAllText(originalFilePath, "restored file");

        // Backup file is missing, but original exists (was restored externally)
        var backup = new StartupEntryBackup(
            Id: "startup:restored",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: originalFilePath, // Exists
            FileBackupPath: Path.Combine(backupDir, "restored.lnk"), // Missing
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        _store.Save(backup);

        var removed = _store.CleanupStaleBackups();

        // Should NOT remove because original file exists
        Assert.Equal(0, removed);
        Assert.Single(_store.GetAll());
    }

    [Fact]
    public void CleanupStaleBackups_DoesNotAffectNonStartupFolderEntries()
    {
        // RunKey entries don't use file backup, so they should never be considered stale
        var runKeyBackup = CreateRunKeyBackup("run:test", "TestEntry");
        _store.Save(runKeyBackup);

        var removed = _store.CleanupStaleBackups();

        Assert.Equal(0, removed);
        Assert.Single(_store.GetAll());
    }

    [Fact]
    public void CleanupStaleBackups_HandlesMultipleEntriesMixed()
    {
        var backupDir = Path.Combine(_tempRoot, "StartupFiles");
        Directory.CreateDirectory(backupDir);
        var existingBackupPath = Path.Combine(backupDir, "exists.lnk");
        File.WriteAllText(existingBackupPath, "dummy");

        // Valid StartupFolder with existing backup
        _store.Save(new StartupEntryBackup(
            Id: "startup:valid",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: "C:\\Startup\\valid.lnk",
            FileBackupPath: existingBackupPath,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow));

        // Stale StartupFolder with missing backup
        _store.Save(new StartupEntryBackup(
            Id: "startup:stale",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: "C:\\Startup\\stale.lnk",
            FileBackupPath: Path.Combine(backupDir, "missing.lnk"),
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow));

        // RunKey backup (should not be affected)
        _store.Save(CreateRunKeyBackup("run:entry", "RunEntry"));

        Assert.Equal(3, _store.GetAll().Count);

        var removed = _store.CleanupStaleBackups();

        Assert.Equal(1, removed);
        Assert.Equal(2, _store.GetAll().Count);
        Assert.NotNull(_store.Get("startup:valid"));
        Assert.Null(_store.Get("startup:stale"));
        Assert.NotNull(_store.Get("run:entry"));
    }

    #endregion

    #region FindLatestByValueName Tests

    [Fact]
    public void FindLatestByValueName_ReturnsLatestMatch()
    {
        var older = new StartupEntryBackup(
            Id: "run:old",
            SourceKind: StartupItemSourceKind.RunKey,
            RegistryRoot: "HKCU",
            RegistrySubKey: "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            RegistryValueName: "SharedName",
            RegistryValueData: "old.exe",
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow.AddHours(-1));

        var newer = new StartupEntryBackup(
            Id: "run:new",
            SourceKind: StartupItemSourceKind.RunKey,
            RegistryRoot: "HKCU",
            RegistrySubKey: "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            RegistryValueName: "SharedName",
            RegistryValueData: "new.exe",
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        _store.Save(older);
        _store.Save(newer);

        var found = _store.FindLatestByValueName("SharedName");

        Assert.NotNull(found);
        Assert.Equal("run:new", found.Id);
    }

    [Fact]
    public void FindLatestByValueName_IsCaseInsensitive()
    {
        _store.Save(CreateRunKeyBackup("run:test", "MyEntry"));

        var found = _store.FindLatestByValueName("MYENTRY");

        Assert.NotNull(found);
    }

    #endregion

    #region Persistence Tests

    [Fact]
    public void Backups_PersistAcrossInstances()
    {
        var backup = CreateRunKeyBackup("persistent-id", "PersistentEntry");
        _store.Save(backup);

        // Create a new store instance pointing to the same directory
        var newStore = new StartupBackupStore(_tempRoot);
        var retrieved = newStore.Get("persistent-id");

        Assert.NotNull(retrieved);
        Assert.Equal("PersistentEntry", retrieved.RegistryValueName);
    }

    [Fact]
    public void BackupDirectory_ReturnsConfiguredPath()
    {
        Assert.Equal(_tempRoot, _store.BackupDirectory);
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    #endregion
}
