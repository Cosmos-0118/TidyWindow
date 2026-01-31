using System;
using System.Collections.Generic;
using System.Linq;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Startup;
using Xunit;

namespace TidyWindow.App.Tests;

/// <summary>
/// Tests for deduplication and data integrity in the startup controller.
/// These tests verify that backup entries are correctly filtered and merged
/// without creating duplicates or ghost data.
/// </summary>
public sealed class StartupControllerDeduplicationTests
{
    #region Live Entry ID Filtering

    [Fact]
    public void BackupEntries_WithMatchingLiveIds_AreFiltered()
    {
        // Simulate the ViewModel logic for filtering backups
        var liveEntries = new List<StartupEntryItemViewModel>
        {
            CreateLiveEntry("run:entry-1", "Entry1"),
            CreateLiveEntry("run:entry-2", "Entry2"),
        };

        var backups = new List<StartupEntryBackup>
        {
            CreateRunKeyBackup("run:entry-1", "Entry1"), // Matches live
            CreateRunKeyBackup("run:entry-3", "Entry3"), // Does not match
        };

        var liveIds = new HashSet<string>(liveEntries.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
        var backupOnlyEntries = backups
            .Where(backup => !liveIds.Contains(backup.Id))
            .Select(backup => new StartupEntryItemViewModel(backup))
            .ToList();

        Assert.Single(backupOnlyEntries);
        Assert.Equal("run:entry-3", backupOnlyEntries[0].Item.Id);
    }

    [Fact]
    public void BackupEntries_CaseInsensitiveMatching_FiltersCorrectly()
    {
        var liveEntries = new List<StartupEntryItemViewModel>
        {
            CreateLiveEntry("RUN:ENTRY-1", "Entry1"),
        };

        var backups = new List<StartupEntryBackup>
        {
            CreateRunKeyBackup("run:entry-1", "Entry1"), // Different case, should match
        };

        var liveIds = new HashSet<string>(liveEntries.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
        var backupOnlyEntries = backups
            .Where(backup => !liveIds.Contains(backup.Id))
            .ToList();

        Assert.Empty(backupOnlyEntries);
    }

    #endregion

    #region Validation Filtering

    [Fact]
    public void InvalidBackups_AreFilteredOut()
    {
        var backups = new List<StartupEntryBackup?>
        {
            CreateRunKeyBackup("valid-1", "ValidEntry"),
            CreateInvalidBackup("invalid-1"),
            null,
        };

        var validBackups = backups
            .Where(backup => StartupBackupStore.IsValidBackup(backup))
            .ToList();

        Assert.Single(validBackups);
        Assert.Equal("valid-1", validBackups[0]!.Id);
    }

    [Fact]
    public void BackupsWithEmptyId_AreFilteredOut()
    {
        var backup = new StartupEntryBackup(
            Id: "",
            SourceKind: StartupItemSourceKind.RunKey,
            RegistryRoot: "HKCU",
            RegistrySubKey: "Run",
            RegistryValueName: "Test",
            RegistryValueData: "test.exe",
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
    public void BackupsWithWhitespaceId_AreFilteredOut()
    {
        var backup = new StartupEntryBackup(
            Id: "   ",
            SourceKind: StartupItemSourceKind.RunKey,
            RegistryRoot: "HKCU",
            RegistrySubKey: "Run",
            RegistryValueName: "Test",
            RegistryValueData: "test.exe",
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

    #region Deduplication Within Backups

    [Fact]
    public void DuplicateBackupIds_AreDeduplicatedUsingHashSet()
    {
        var backups = new List<StartupEntryBackup>
        {
            CreateRunKeyBackup("dup-id", "Entry1"),
            CreateRunKeyBackup("dup-id", "Entry2"), // Duplicate ID
            CreateRunKeyBackup("unique-id", "Entry3"),
        };

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = backups
            .Where(backup => seenIds.Add(backup.Id))
            .ToList();

        Assert.Equal(2, deduplicated.Count);
        Assert.Contains(deduplicated, b => b.Id == "dup-id");
        Assert.Contains(deduplicated, b => b.Id == "unique-id");
    }

    [Fact]
    public void DuplicateBackupIds_CaseInsensitive_AreDeduplicatedCorrectly()
    {
        var backups = new List<StartupEntryBackup>
        {
            CreateRunKeyBackup("ID-1", "Entry1"),
            CreateRunKeyBackup("id-1", "Entry2"), // Same ID, different case
            CreateRunKeyBackup("Id-1", "Entry3"), // Same ID, different case
        };

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = backups
            .Where(backup => seenIds.Add(backup.Id))
            .ToList();

        Assert.Single(deduplicated);
    }

    #endregion

    #region UI Merge Deduplication

    [Fact]
    public void MergingBackupEntries_PreventsExistingIdDuplication()
    {
        // Simulate filtered live items
        var filteredItems = new List<StartupEntryItemViewModel>
        {
            CreateLiveEntry("entry-1", "LiveEntry1"),
            CreateLiveEntry("entry-2", "LiveEntry2"),
        };

        // Simulate backup entries (one matches live, one doesn't)
        var backupEntries = new List<StartupEntryItemViewModel>
        {
            CreateBackupEntry("entry-1", "BackupEntry1"), // Matches live
            CreateBackupEntry("entry-3", "BackupEntry3"), // New
        };

        // Apply deduplication like the RefreshView method
        var existingIds = new HashSet<string>(filteredItems.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
        var safeBackupEntries = backupEntries
            .Where(b => !existingIds.Contains(b.Item.Id))
            .ToList();

        filteredItems.AddRange(safeBackupEntries);

        Assert.Equal(3, filteredItems.Count);
        Assert.Single(filteredItems.Where(e => e.Item.Id == "entry-1"));
        Assert.Single(filteredItems.Where(e => e.Item.Id == "entry-2"));
        Assert.Single(filteredItems.Where(e => e.Item.Id == "entry-3"));
    }

    [Fact]
    public void MergingBackupEntries_EmptyFilteredItems_AddsAllBackups()
    {
        var filteredItems = new List<StartupEntryItemViewModel>();
        var backupEntries = new List<StartupEntryItemViewModel>
        {
            CreateBackupEntry("backup-1", "Backup1"),
            CreateBackupEntry("backup-2", "Backup2"),
        };

        var existingIds = new HashSet<string>(filteredItems.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
        var safeBackupEntries = backupEntries
            .Where(b => !existingIds.Contains(b.Item.Id))
            .ToList();

        filteredItems.AddRange(safeBackupEntries);

        Assert.Equal(2, filteredItems.Count);
    }

    [Fact]
    public void MergingBackupEntries_AllMatchingIds_AddsNothing()
    {
        var filteredItems = new List<StartupEntryItemViewModel>
        {
            CreateLiveEntry("entry-1", "Live1"),
            CreateLiveEntry("entry-2", "Live2"),
        };
        var backupEntries = new List<StartupEntryItemViewModel>
        {
            CreateBackupEntry("entry-1", "Backup1"),
            CreateBackupEntry("entry-2", "Backup2"),
        };

        var existingIds = new HashSet<string>(filteredItems.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
        var safeBackupEntries = backupEntries
            .Where(b => !existingIds.Contains(b.Item.Id))
            .ToList();

        Assert.Empty(safeBackupEntries);
    }

    #endregion

    #region Full Pipeline Simulation

    [Fact]
    public void FullPipeline_ValidatesAndDeduplicatesCorrectly()
    {
        // Simulate the complete RefreshAsync pipeline
        var liveEntries = new List<StartupEntryItemViewModel>
        {
            CreateLiveEntry("live-1", "Live1"),
            CreateLiveEntry("live-2", "Live2"),
        };

        var allBackups = new List<StartupEntryBackup?>
        {
            CreateRunKeyBackup("live-1", "MatchesLive"), // Should be filtered (matches live)
            CreateRunKeyBackup("backup-1", "BackupOnly"), // Should be kept
            CreateRunKeyBackup("backup-1", "DuplicateBackup"), // Should be deduplicated
            CreateInvalidBackup("invalid"), // Should be filtered (invalid)
            null, // Should be filtered (null)
        };

        var liveIds = new HashSet<string>(liveEntries.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
        var seenBackupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var backupOnlyEntries = allBackups
            .Where(backup => StartupBackupStore.IsValidBackup(backup))
            .Where(backup => !liveIds.Contains(backup!.Id))
            .Where(backup => seenBackupIds.Add(backup!.Id))
            .Select(backup => new StartupEntryItemViewModel(backup!))
            .ToList();

        Assert.Single(backupOnlyEntries);
        Assert.Equal("backup-1", backupOnlyEntries[0].Item.Id);
        Assert.True(backupOnlyEntries[0].IsBackupOnly);
    }

    [Fact]
    public void FullPipeline_EmptyLiveEntries_KeepsAllValidBackups()
    {
        var liveEntries = new List<StartupEntryItemViewModel>();

        var allBackups = new List<StartupEntryBackup>
        {
            CreateRunKeyBackup("backup-1", "Backup1"),
            CreateRunKeyBackup("backup-2", "Backup2"),
            CreateRunKeyBackup("backup-3", "Backup3"),
        };

        var liveIds = new HashSet<string>(liveEntries.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
        var seenBackupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var backupOnlyEntries = allBackups
            .Where(backup => StartupBackupStore.IsValidBackup(backup))
            .Where(backup => !liveIds.Contains(backup.Id))
            .Where(backup => seenBackupIds.Add(backup.Id))
            .Select(backup => new StartupEntryItemViewModel(backup))
            .ToList();

        Assert.Equal(3, backupOnlyEntries.Count);
    }

    [Fact]
    public void FullPipeline_AllBackupsMatchLive_ReturnsEmpty()
    {
        var liveEntries = new List<StartupEntryItemViewModel>
        {
            CreateLiveEntry("id-1", "Live1"),
            CreateLiveEntry("id-2", "Live2"),
        };

        var allBackups = new List<StartupEntryBackup>
        {
            CreateRunKeyBackup("id-1", "Backup1"),
            CreateRunKeyBackup("id-2", "Backup2"),
        };

        var liveIds = new HashSet<string>(liveEntries.Select(e => e.Item.Id), StringComparer.OrdinalIgnoreCase);
        var seenBackupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var backupOnlyEntries = allBackups
            .Where(backup => StartupBackupStore.IsValidBackup(backup))
            .Where(backup => !liveIds.Contains(backup.Id))
            .Where(backup => seenBackupIds.Add(backup.Id))
            .Select(backup => new StartupEntryItemViewModel(backup))
            .ToList();

        Assert.Empty(backupOnlyEntries);
    }

    #endregion

    #region Helpers

    private static StartupEntryItemViewModel CreateLiveEntry(string id, string name)
    {
        var item = new StartupItem(
            Id: id,
            Name: name,
            ExecutablePath: "C:\\tools\\app.exe",
            SourceKind: StartupItemSourceKind.RunKey,
            SourceTag: "HKCU Run",
            Arguments: null,
            RawCommand: null,
            IsEnabled: true,
            EntryLocation: "HKCU\\Run",
            Publisher: "Publisher",
            SignatureStatus: StartupSignatureStatus.SignedTrusted,
            Impact: StartupImpact.Medium,
            FileSizeBytes: 1024,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            UserContext: "CurrentUser");

        return new StartupEntryItemViewModel(item);
    }

    private static StartupEntryItemViewModel CreateBackupEntry(string id, string name)
    {
        var backup = CreateRunKeyBackup(id, name);
        return new StartupEntryItemViewModel(backup);
    }

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

    private static StartupEntryBackup CreateInvalidBackup(string id)
    {
        // Backup with no identifying fields (invalid)
        return new StartupEntryBackup(
            Id: id,
            SourceKind: StartupItemSourceKind.RunKey,
            RegistryRoot: "HKCU",
            RegistrySubKey: "Run",
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
    }

    #endregion
}
