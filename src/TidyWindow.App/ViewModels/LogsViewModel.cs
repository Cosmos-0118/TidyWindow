using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Infrastructure;
using TidyWindow.App.Services;
using WpfApplication = System.Windows.Application;
using WindowsClipboard = System.Windows.Clipboard;

namespace TidyWindow.App.ViewModels;

public sealed partial class LogsViewModel : ViewModelBase, IDisposable
{
    private readonly ActivityLogService _logService;
    private readonly ObservableCollection<ActivityLogItemViewModel> _entries;
    private readonly UiDebounceDispatcher _searchRefreshDebounce;

    public LogsViewModel(ActivityLogService logService)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));

        var snapshot = _logService.GetSnapshot();
        _entries = new ObservableCollection<ActivityLogItemViewModel>(snapshot.Select(ActivityLogItemViewModel.FromEntry));

        EntriesView = CollectionViewSource.GetDefaultView(_entries);
        EntriesView.SortDescriptions.Add(new SortDescription(nameof(ActivityLogItemViewModel.Timestamp), ListSortDirection.Descending));
        EntriesView.Filter = FilterEntry;

        LevelFilterOptions = new List<ActivityLogLevel?>
        {
            null,
            ActivityLogLevel.Information,
            ActivityLogLevel.Success,
            ActivityLogLevel.Warning,
            ActivityLogLevel.Error
        };

        if (_entries.Count > 0)
        {
            SelectedEntry = _entries[0];
        }

        _logService.EntryAdded += OnEntryAdded;
        _searchRefreshDebounce = new UiDebounceDispatcher(TimeSpan.FromMilliseconds(110));
    }

    public ICollectionView EntriesView { get; }

    public IReadOnlyList<ActivityLogLevel?> LevelFilterOptions { get; }

    [ObservableProperty]
    private ActivityLogLevel? _selectedLevel;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private ActivityLogItemViewModel? _selectedEntry;

    [ObservableProperty]
    private bool _isAutoSelectLatest = true;

    partial void OnSelectedLevelChanged(ActivityLogLevel? oldValue, ActivityLogLevel? newValue)
    {
        EntriesView.Refresh();
    }

    partial void OnSearchTextChanged(string? oldValue, string? newValue)
    {
        _searchRefreshDebounce.Schedule(EntriesView.Refresh);
    }

    private bool FilterEntry(object? item)
    {
        if (item is not ActivityLogItemViewModel entry)
        {
            return false;
        }

        if (SelectedLevel is ActivityLogLevel level && entry.Level != level)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var filter = SearchText.Trim();
        return entry.Matches(filter);
    }

    private void OnEntryAdded(object? sender, ActivityLogEventArgs e)
    {
        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => AddEntry(e.Entry));
        }
        else
        {
            AddEntry(e.Entry);
        }
    }

    private void AddEntry(ActivityLogEntry entry)
    {
        var viewModel = ActivityLogItemViewModel.FromEntry(entry);
        _entries.Insert(0, viewModel);

        while (_entries.Count > _logService.Capacity)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }

        if (IsAutoSelectLatest)
        {
            SelectedEntry = viewModel;
        }
    }

    [RelayCommand]
    private void CopyEntry(ActivityLogItemViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            WindowsClipboard.SetText(entry.BuildClipboardText());
        }
        catch
        {
            // Clipboard may not be available (for example, in remote sessions).
        }
    }

    public void Dispose()
    {
        _logService.EntryAdded -= OnEntryAdded;
        _searchRefreshDebounce.Flush();
        _searchRefreshDebounce.Dispose();
    }
}

public sealed class ActivityLogItemViewModel
{
    private ActivityLogItemViewModel(ActivityLogEntry entry)
    {
        Timestamp = entry.Timestamp;
        Level = entry.Level;
        Source = entry.Source;
        Message = entry.Message;
        Details = entry.Details;
    }

    public static ActivityLogItemViewModel FromEntry(ActivityLogEntry entry)
    {
        return new ActivityLogItemViewModel(entry);
    }

    public DateTimeOffset Timestamp { get; }

    public ActivityLogLevel Level { get; }

    public string Source { get; }

    public string Message { get; }

    public IReadOnlyList<string> Details { get; }

    public bool HasDetails => Details.Count > 0;

    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string LevelDisplay => Level switch
    {
        ActivityLogLevel.Information => "Info",
        ActivityLogLevel.Success => "Success",
        ActivityLogLevel.Warning => "Warning",
        ActivityLogLevel.Error => "Error",
        _ => Level.ToString()
    };

    public bool Matches(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (Source.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Message.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Details.Any(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    public string BuildClipboardText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} [{Level}] {Source}");
        builder.AppendLine(Message);

        foreach (var line in Details)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }
}
