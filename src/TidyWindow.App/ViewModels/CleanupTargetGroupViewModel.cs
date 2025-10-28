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
    private int _selectionNotificationSuppression;
    private bool _selectionDirty;

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

        if (_selectionNotificationSuppression > 0)
        {
            _selectionDirty = true;
            return;
        }

        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSizeBytes));
        OnPropertyChanged(nameof(SelectedSizeMegabytes));
    }

    public IDisposable BeginSelectionUpdate()
    {
        _selectionNotificationSuppression++;
        return new SelectionUpdateScope(this);
    }

    private void EndSelectionUpdate()
    {
        if (_selectionNotificationSuppression == 0)
        {
            return;
        }

        _selectionNotificationSuppression--;

        if (_selectionNotificationSuppression == 0 && _selectionDirty)
        {
            _selectionDirty = false;
            NotifySelectionChanged();
        }
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

    private sealed class SelectionUpdateScope : IDisposable
    {
        private readonly CleanupTargetGroupViewModel _owner;
        private bool _disposed;

        public SelectionUpdateScope(CleanupTargetGroupViewModel owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.EndSelectionUpdate();
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

    public DateTime LastAccessLocal => Model.LastAccessUtc == DateTime.MinValue ? DateTime.MinValue : Model.LastAccessUtc.ToLocalTime();

    public DateTime CreationLocal => Model.CreationUtc == DateTime.MinValue ? DateTime.MinValue : Model.CreationUtc.ToLocalTime();

    public bool HasLastAccess => Model.LastAccessUtc != DateTime.MinValue;

    public bool HasCreation => Model.CreationUtc != DateTime.MinValue;

    public string LastModifiedDisplay => FormatTimestamp(Model.LastModifiedUtc);

    public string LastAccessDisplay => FormatTimestamp(Model.LastAccessUtc);

    public string CreationDisplay => FormatTimestamp(Model.CreationUtc);

    public long SizeBytes => Model.SizeBytes;

    public double SizeMegabytes => Model.SizeMegabytes;

    public bool IsDirectory => Model.IsDirectory;

    public string Extension => Model.Extension;

    public bool IsHidden => Model.IsHidden;

    public bool IsSystem => Model.IsSystem;

    public bool WasModifiedRecently => Model.WasModifiedRecently;

    public double Confidence => Model.Confidence;

    public bool HasSignals => Model.HasSignals;

    public IReadOnlyList<string> Signals => Model.Signals;

    public string SignalsSummary => HasSignals ? string.Join(", ", Model.Signals) : string.Empty;

    public string ConfidenceDescription => Confidence switch
    {
        >= 0.85 => "Very high confidence",
        >= 0.65 => "High confidence",
        >= 0.45 => "Moderate confidence",
        >= 0.25 => "Low confidence",
        _ => "Minimal signals"
    };

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

    private static string FormatTimestamp(DateTime utc)
    {
        if (utc == DateTime.MinValue || utc == DateTime.MaxValue)
        {
            return "--";
        }

        return utc.ToLocalTime().ToString("g");
    }
}
