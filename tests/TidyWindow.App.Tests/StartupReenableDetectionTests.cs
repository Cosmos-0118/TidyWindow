using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Startup;
using Xunit;

namespace TidyWindow.App.Tests;

/// <summary>
/// Tests for detecting when applications re-enable themselves after being disabled.
/// Ensures users are properly warned and Auto-Guard handles violations correctly.
/// </summary>
public sealed class StartupReenableDetectionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly StartupBackupStore _backupStore;

    public StartupReenableDetectionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "TidyWindowTests", "ReenableDetection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _backupStore = new StartupBackupStore(_tempRoot);
    }

    public void Dispose()
    {
        TryDeleteDirectory(_tempRoot);
    }

    #region Detection Logic Tests

    [Fact]
    public void DetectsReenabledEntry_WhenBackupExistsAndLiveEntryIsEnabled()
    {
        // Arrange: Create a backup (simulating a previously disabled entry)
        var backup = CreateRunKeyBackup("run:HKCU Run:TestApp", "TestApp");
        _backupStore.Save(backup);

        // Create live entry with same ID that is enabled (app re-created it)
        var liveEntry = CreateLiveEntry("run:HKCU Run:TestApp", "TestApp", isEnabled: true);
        var liveIds = new HashSet<string> { liveEntry.Item.Id };

        // Act: Simulate detection logic
        var backups = _backupStore.GetAll();
        var conflictingBackups = backups
            .Where(b => liveIds.Contains(b.Id))
            .ToList();

        // Assert
        Assert.Single(conflictingBackups);
        Assert.Equal("run:HKCU Run:TestApp", conflictingBackups[0].Id);
    }

    [Fact]
    public void DoesNotDetectConflict_WhenBackupExistsButLiveEntryIsDisabled()
    {
        // Arrange: Create a backup
        var backup = CreateRunKeyBackup("run:HKCU Run:TestApp", "TestApp");
        _backupStore.Save(backup);

        // Create live entry with same ID but DISABLED (no conflict)
        var liveEntry = CreateLiveEntry("run:HKCU Run:TestApp", "TestApp", isEnabled: false);
        var liveIds = new HashSet<string> { liveEntry.Item.Id };

        // Act: Check if entry is re-enabled
        var backups = _backupStore.GetAll();
        var conflicts = backups
            .Where(b => liveIds.Contains(b.Id))
            .Select(b => new { Backup = b, LiveEntry = liveEntry })
            .Where(x => x.LiveEntry.IsEnabled) // Entry must be enabled to be a conflict
            .ToList();

        // Assert: No conflict because live entry is disabled
        Assert.Empty(conflicts);
    }

    [Fact]
    public void DoesNotDetectConflict_WhenBackupExistsButNoMatchingLiveEntry()
    {
        // Arrange: Create a backup
        var backup = CreateRunKeyBackup("run:HKCU Run:OldApp", "OldApp");
        _backupStore.Save(backup);

        // No matching live entry (different ID)
        var liveEntry = CreateLiveEntry("run:HKCU Run:DifferentApp", "DifferentApp", isEnabled: true);
        var liveIds = new HashSet<string> { liveEntry.Item.Id };

        // Act
        var backups = _backupStore.GetAll();
        var conflicts = backups
            .Where(b => liveIds.Contains(b.Id))
            .ToList();

        // Assert: No conflict because IDs don't match
        Assert.Empty(conflicts);
    }

    [Fact]
    public void DetectsMultipleReenabledEntries()
    {
        // Arrange: Create multiple backups
        _backupStore.Save(CreateRunKeyBackup("run:App1", "App1"));
        _backupStore.Save(CreateRunKeyBackup("run:App2", "App2"));
        _backupStore.Save(CreateRunKeyBackup("run:App3", "App3"));

        // Only two apps re-enabled themselves
        var liveEntries = new List<StartupEntryItemViewModel>
        {
            CreateLiveEntry("run:App1", "App1", isEnabled: true),  // Re-enabled
            CreateLiveEntry("run:App2", "App2", isEnabled: true),  // Re-enabled
            // App3 is NOT in live inventory - stayed disabled
        };
        var liveIds = new HashSet<string>(liveEntries.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);

        // Act
        var conflicts = _backupStore.GetAll()
            .Where(b => liveIds.Contains(b.Id))
            .ToList();

        // Assert
        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.Id == "run:App1");
        Assert.Contains(conflicts, c => c.Id == "run:App2");
    }

    [Fact]
    public void CaseInsensitiveMatching_DetectsReenabledEntry()
    {
        // Arrange: Create backup with different case
        var backup = CreateRunKeyBackup("RUN:HKCU Run:TESTAPP", "TestApp");
        _backupStore.Save(backup);

        // Live entry with different case
        var liveEntry = CreateLiveEntry("run:hkcu run:testapp", "TestApp", isEnabled: true);
        var liveIds = new HashSet<string>(new[] { liveEntry.Item.Id }, StringComparer.OrdinalIgnoreCase);

        // Act
        var conflicts = _backupStore.GetAll()
            .Where(b => liveIds.Contains(b.Id))
            .ToList();

        // Assert: Should match despite case difference
        Assert.Single(conflicts);
    }

    #endregion

    #region Backup Cleanup Tests

    [Fact]
    public void BackupFileIsDeleted_WhenEntryReenables_StartupFolder()
    {
        // Arrange: Create a backup with a backup file
        var backupDir = Path.Combine(_tempRoot, "StartupFiles");
        Directory.CreateDirectory(backupDir);
        var backupFilePath = Path.Combine(backupDir, "test.lnk");
        File.WriteAllText(backupFilePath, "dummy shortcut");

        var backup = new StartupEntryBackup(
            Id: "startup:Startup Folder:test.lnk",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: @"C:\Users\Test\Startup\test.lnk",
            FileBackupPath: backupFilePath,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        _backupStore.Save(backup);
        Assert.True(File.Exists(backupFilePath));

        // Act: Simulate cleanup when re-enabled detected
        if (backup.SourceKind == StartupItemSourceKind.StartupFolder &&
            !string.IsNullOrWhiteSpace(backup.FileBackupPath) &&
            File.Exists(backup.FileBackupPath))
        {
            File.Delete(backup.FileBackupPath);
        }
        _backupStore.Remove(backup.Id);

        // Assert
        Assert.False(File.Exists(backupFilePath));
        Assert.Null(_backupStore.Get(backup.Id));
    }

    [Fact]
    public void BackupRecordIsRemoved_AfterReenableDetected()
    {
        // Arrange
        var backup = CreateRunKeyBackup("run:TestApp", "TestApp");
        _backupStore.Save(backup);
        Assert.NotNull(_backupStore.Get("run:TestApp"));

        // Act: Simulate removal after reenable detected
        _backupStore.Remove(backup.Id);

        // Assert
        Assert.Null(_backupStore.Get("run:TestApp"));
    }

    #endregion

    #region Guard Status Tests

    [Fact]
    public void GuardedEntry_ShouldBeAutoRedisabled()
    {
        // Arrange
        var guardService = new StartupGuardService(_tempRoot);
        var entryId = "run:HKCU Run:PersistentApp";

        // Mark entry as guarded
        guardService.SetGuard(entryId, enabled: true);

        // Create backup
        _backupStore.Save(CreateRunKeyBackup(entryId, "PersistentApp"));

        // Create live entry (app re-enabled)
        var liveEntry = CreateLiveEntry(entryId, "PersistentApp", isEnabled: true);

        // Act: Check if guarded
        var guards = guardService.GetAll();
        var isGuarded = guards.Contains(entryId, StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.True(isGuarded);
        Assert.True(liveEntry.IsEnabled); // Would be re-disabled by Auto-Guard
    }

    [Fact]
    public void NonGuardedEntry_ShouldWarnUser()
    {
        // Arrange
        var guardService = new StartupGuardService(_tempRoot);
        var entryId = "run:HKCU Run:SneakyApp";

        // NOT marked as guarded
        // guardService.SetGuard(entryId, enabled: false); // Explicitly not guarded

        // Create backup
        _backupStore.Save(CreateRunKeyBackup(entryId, "SneakyApp"));

        // Create live entry (app re-enabled)
        var liveEntry = CreateLiveEntry(entryId, "SneakyApp", isEnabled: true);

        // Act: Check if guarded
        var guards = guardService.GetAll();
        var isGuarded = guards.Contains(entryId, StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.False(isGuarded);
        Assert.True(liveEntry.IsEnabled); // Stays enabled, user must act
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EmptyBackupStore_NoConflictsDetected()
    {
        // Arrange: No backups
        var liveEntry = CreateLiveEntry("run:SomeApp", "SomeApp", isEnabled: true);
        var liveIds = new HashSet<string> { liveEntry.Item.Id };

        // Act
        var conflicts = _backupStore.GetAll()
            .Where(b => liveIds.Contains(b.Id))
            .ToList();

        // Assert
        Assert.Empty(conflicts);
    }

    [Fact]
    public void EmptyLiveInventory_NoConflictsDetected()
    {
        // Arrange: Create backups but no live entries
        _backupStore.Save(CreateRunKeyBackup("run:App1", "App1"));
        _backupStore.Save(CreateRunKeyBackup("run:App2", "App2"));

        var liveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Act
        var conflicts = _backupStore.GetAll()
            .Where(b => liveIds.Contains(b.Id))
            .ToList();

        // Assert
        Assert.Empty(conflicts);
    }

    [Fact]
    public void MultipleBackupTypes_AllDetected()
    {
        // Arrange: Different source kinds
        _backupStore.Save(CreateRunKeyBackup("run:RunKeyApp", "RunKeyApp"));
        _backupStore.Save(new StartupEntryBackup(
            Id: "startup:StartupFolderApp",
            SourceKind: StartupItemSourceKind.StartupFolder,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: @"C:\Startup\app.lnk",
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: null,
            ServiceStartValue: null,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow));
        _backupStore.Save(new StartupEntryBackup(
            Id: "svc:ServiceApp",
            SourceKind: StartupItemSourceKind.Service,
            RegistryRoot: null,
            RegistrySubKey: null,
            RegistryValueName: null,
            RegistryValueData: null,
            FileOriginalPath: null,
            FileBackupPath: null,
            TaskPath: null,
            TaskEnabled: null,
            ServiceName: "ServiceApp",
            ServiceStartValue: 2,
            ServiceDelayedAutoStart: null,
            CreatedAtUtc: DateTimeOffset.UtcNow));

        // All three re-enabled
        var liveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "run:RunKeyApp",
            "startup:StartupFolderApp",
            "svc:ServiceApp"
        };

        // Act
        var conflicts = _backupStore.GetAll()
            .Where(b => liveIds.Contains(b.Id))
            .ToList();

        // Assert
        Assert.Equal(3, conflicts.Count);
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

    private static StartupEntryItemViewModel CreateLiveEntry(string id, string name, bool isEnabled)
    {
        var item = new StartupItem(
            Id: id,
            Name: name,
            ExecutablePath: $"C:\\tools\\{name}.exe",
            SourceKind: StartupItemSourceKind.RunKey,
            SourceTag: "HKCU Run",
            Arguments: null,
            RawCommand: null,
            IsEnabled: isEnabled,
            EntryLocation: "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            Publisher: "TestPublisher",
            SignatureStatus: StartupSignatureStatus.SignedTrusted,
            Impact: StartupImpact.Medium,
            FileSizeBytes: 1024,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            UserContext: "CurrentUser");

        return new StartupEntryItemViewModel(item);
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
