namespace TidyWindow.Core.Startup;

public sealed record StartupToggleResult(bool Succeeded, StartupItem Item, StartupEntryBackup? Backup, string? ErrorMessage);
