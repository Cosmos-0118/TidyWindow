using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.ViewModels.Preview;

public sealed class PreviewPagingController
{
    private readonly Func<CleanupTargetGroupViewModel?> _selectedTargetProvider;
    private readonly IPreviewFilter _filter;

    private int _currentPage = 1;
    private int _pageSize = 100;
    private int _totalFilteredItems;
    private CleanupPreviewSortMode _previewSortMode = CleanupPreviewSortMode.Impact;
    private int _selectRangeStartPage = 1;
    private int _selectRangeEndPage = 1;

    public PreviewPagingController(Func<CleanupTargetGroupViewModel?> selectedTargetProvider, IPreviewFilter filter)
    {
        _selectedTargetProvider = selectedTargetProvider ?? throw new ArgumentNullException(nameof(selectedTargetProvider));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
    }

    public ObservableCollection<CleanupPreviewItemViewModel> FilteredItems { get; } = new();

    public event EventHandler? StateChanged;

    public int CurrentPage => _currentPage;

    public int PageSize => _pageSize;

    public int TotalFilteredItems => _totalFilteredItems;

    public int TotalPages => ComputeTotalPages(_totalFilteredItems, _pageSize);

    public string PageDisplay => _totalFilteredItems == 0
        ? "Page 0 of 0"
        : $"Page {_currentPage} of {TotalPages}";

    public bool CanGoToPreviousPage => _currentPage > 1;

    public bool CanGoToNextPage => _currentPage < TotalPages;

    public int SelectRangeStartPage => _selectRangeStartPage;

    public int SelectRangeEndPage => _selectRangeEndPage;

    public CleanupPreviewSortMode PreviewSortMode => _previewSortMode;

    public bool TrySetCurrentPage(int value)
    {
        var newValue = Math.Clamp(value, 1, Math.Max(1, TotalPages));
        if (_currentPage == newValue)
        {
            return false;
        }

        _currentPage = newValue;
        Refresh();
        return true;
    }

    public bool TrySetPageSize(int value)
    {
        var sanitized = Math.Max(1, value);
        if (_pageSize == sanitized)
        {
            return false;
        }

        _pageSize = sanitized;
        _currentPage = 1;
        Refresh();
        return true;
    }

    public bool TrySetPreviewSortMode(CleanupPreviewSortMode mode)
    {
        if (_previewSortMode == mode)
        {
            return false;
        }

        _previewSortMode = mode;
        Refresh();
        return true;
    }

    public bool TrySetSelectRangeStartPage(int value)
    {
        var sanitized = Math.Max(1, value);
        if (_selectRangeStartPage == sanitized)
        {
            return false;
        }

        _selectRangeStartPage = sanitized;
        RaiseStateChanged();
        return true;
    }

    public bool TrySetSelectRangeEndPage(int value)
    {
        var sanitized = Math.Max(1, value);
        if (_selectRangeEndPage == sanitized)
        {
            return false;
        }

        _selectRangeEndPage = sanitized;
        RaiseStateChanged();
        return true;
    }

    public bool TryGoToNextPage()
    {
        if (!CanGoToNextPage)
        {
            return false;
        }

        _currentPage++;
        Refresh();
        return true;
    }

    public bool TryGoToPreviousPage()
    {
        if (!CanGoToPreviousPage)
        {
            return false;
        }

        _currentPage--;
        Refresh();
        return true;
    }

    public void Refresh()
    {
        FilteredItems.Clear();

        var target = _selectedTargetProvider();
        if (target is null)
        {
            _totalFilteredItems = 0;
            _currentPage = 1;
            RaiseStateChanged();
            return;
        }

        var filtered = ApplySorting(target.Items.Where(_filter.Matches)).ToList();
        _totalFilteredItems = filtered.Count;

        var totalPages = Math.Max(1, TotalPages);
        var newPage = Math.Clamp(_currentPage, 1, totalPages);
        if (newPage != _currentPage)
        {
            _currentPage = newPage;
        }

        var pageSize = Math.Max(1, _pageSize);
        var skip = (_currentPage - 1) * pageSize;

        foreach (var item in filtered.Skip(skip).Take(pageSize))
        {
            FilteredItems.Add(item);
        }

        RaiseStateChanged();
    }

    public void Reset()
    {
        FilteredItems.Clear();
        _totalFilteredItems = 0;
        _currentPage = 1;
        RaiseStateChanged();
    }

    public void ResetCurrentPage()
    {
        if (_currentPage == 1)
        {
            return;
        }

        _currentPage = 1;
        RaiseStateChanged();
    }

    private IEnumerable<CleanupPreviewItemViewModel> ApplySorting(IEnumerable<CleanupPreviewItemViewModel> items)
    {
        return _previewSortMode switch
        {
            CleanupPreviewSortMode.Impact => items.OrderByDescending(static item => item.SizeBytes),
            CleanupPreviewSortMode.Newest => items.OrderByDescending(static item => item.LastModifiedLocal),
            CleanupPreviewSortMode.Risk => items.OrderBy(static item => item.Confidence),
            _ => items
        };
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static int ComputeTotalPages(int itemCount, int pageSize)
    {
        if (itemCount <= 0)
        {
            return 1;
        }

        var sanitizedPageSize = Math.Max(1, pageSize);
        return (itemCount + sanitizedPageSize - 1) / sanitizedPageSize;
    }
}
