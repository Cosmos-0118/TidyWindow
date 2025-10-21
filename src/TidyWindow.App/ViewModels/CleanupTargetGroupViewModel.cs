using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.Core.Cleanup;
using WindowsClipboard = System.Windows.Clipboard;

namespace TidyWindow.App.ViewModels;

/// <summary>
/// View model wrapper around a cleanup target group so the UI can manage selection and presentation state.
/// </summary>
public sealed partial class CleanupTargetGroupViewModel : ObservableObject, IDisposable
{
    private bool _isDisposed;

    public CleanupTargetGroupViewModel(CleanupTargetReport model)
    {
        Model = model;
        Items = new ObservableCollection<CleanupPreviewItemViewModel>(
            model.Preview.Select(item => new CleanupPreviewItemViewModel(item)));

        Items.CollectionChanged += OnItemsCollectionChanged;
        foreach (var item in Items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    public CleanupTargetReport Model { get; }

    public string Category => Model.Category;

    public string Classification => Model.Classification;

    public string Path => Model.Path;

    public string Notes => Model.Notes;

    public IReadOnlyList<string> Warnings => Model.Warnings;

    public bool HasWarnings => Model.HasWarnings;

    public ObservableCollection<CleanupPreviewItemViewModel> Items { get; }

    public event EventHandler? SelectionChanged;
    public event NotifyCollectionChangedEventHandler? ItemsChanged;

    public int RemainingItemCount => Items.Count;

    public long RemainingSizeBytes => Items.Sum(static item => item.SizeBytes);

    public double RemainingSizeMegabytes => RemainingSizeBytes / 1_048_576d;

    public int SelectedCount => Items.Count(static item => item.IsSelected);

    public long SelectedSizeBytes => Items.Where(static item => item.IsSelected).Sum(static item => item.SizeBytes);

    public double SelectedSizeMegabytes => SelectedSizeBytes / 1_048_576d;

    public IEnumerable<CleanupPreviewItemViewModel> SelectedItems => Items.Where(static item => item.IsSelected);

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (CleanupPreviewItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (CleanupPreviewItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        ItemsChanged?.Invoke(this, e);

        OnPropertyChanged(nameof(RemainingItemCount));
        OnPropertyChanged(nameof(RemainingSizeBytes));
        OnPropertyChanged(nameof(RemainingSizeMegabytes));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSizeBytes));
        OnPropertyChanged(nameof(SelectedSizeMegabytes));
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanupPreviewItemViewModel.IsSelected))
        {
            return;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSizeBytes));
        OnPropertyChanged(nameof(SelectedSizeMegabytes));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        Items.CollectionChanged -= OnItemsCollectionChanged;
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }
}

public sealed partial class CleanupPreviewItemViewModel : ObservableObject
{
    internal CleanupPreviewItemViewModel(CleanupPreviewItem model)
    {
        Model = model;
    }

    public CleanupPreviewItem Model { get; }

    public string Name => Model.Name;

    public string FullName => Model.FullName;

    public string? DirectoryName => Path.GetDirectoryName(Model.FullName);

    public DateTime LastModifiedLocal => Model.LastModifiedUtc.ToLocalTime();

    public long SizeBytes => Model.SizeBytes;

    public double SizeMegabytes => Model.SizeMegabytes;

    public bool IsDirectory => Model.IsDirectory;

    public string Extension => Model.Extension;

    public bool IsHidden => Model.IsHidden;

    public bool IsSystem => Model.IsSystem;

    public bool WasModifiedRecently => Model.WasModifiedRecently;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void CopyPath()
    {
        if (string.IsNullOrWhiteSpace(Model.FullName))
        {
            return;
        }

        try
        {
            WindowsClipboard.SetText(Model.FullName);
        }
        catch
        {
            // Clipboard access can be unavailable in some sessions; ignore failures.
        }
    }
}
