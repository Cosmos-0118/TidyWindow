using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace TidyWindow.App.Services;

/// <summary>
/// Provides LRU-K (K=2) lifetime-aware caching for navigation pages.
/// LRU-K considers both recency and frequency of access to make smarter eviction decisions.
/// Pages accessed frequently are retained longer than pages accessed only once recently.
/// </summary>
public sealed class SmartPageCache : IDisposable
{
    private const int MaxEntries = 6;
    private const int HistoryK = 2; // Track last K accesses (LRU-2)
    private static readonly TimeSpan CorrelatedReferencePeriod = TimeSpan.FromMinutes(2);

    private readonly Dictionary<Type, CachedPageEntry> _entries = new();
    private readonly object _syncRoot = new();
    private bool _isDisposed;
    private Page? _currentPage;

    /// <summary>
    /// Gets the currently active page (for navigation lifecycle management).
    /// </summary>
    public Page? CurrentPage
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentPage;
            }
        }
    }

    public bool TryGetPage(Type pageType, out Page page)
    {
        if (pageType is null)
        {
            throw new ArgumentNullException(nameof(pageType));
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(pageType, out var entry))
            {
                page = null!;
                return false;
            }

            if (entry.IsExpired(DateTimeOffset.UtcNow))
            {
                RemoveEntry(pageType);
                page = null!;
                return false;
            }

            entry.RecordAccess();
            page = entry.Page;
            return true;
        }
    }

    public void StorePage(Type pageType, Page page, PageCachePolicy policy)
    {
        if (pageType is null)
        {
            throw new ArgumentNullException(nameof(pageType));
        }

        if (page is null)
        {
            throw new ArgumentNullException(nameof(page));
        }

        if (policy is null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            RemoveEntry(pageType);
            TrimIfNeeded();
            _entries[pageType] = new CachedPageEntry(page, policy, HistoryK);
        }
    }

    /// <summary>
    /// Sets the currently displayed page and triggers navigation lifecycle callbacks.
    /// </summary>
    public void SetCurrentPage(Page? page)
    {
        Page? previousPage;
        lock (_syncRoot)
        {
            if (ReferenceEquals(_currentPage, page))
            {
                return;
            }

            previousPage = _currentPage;
            _currentPage = page;
        }

        // Notify previous page it's being navigated away from
        if (previousPage is INavigationAware previousAware)
        {
            try
            {
                previousAware.OnNavigatingFrom();
            }
            catch
            {
                // Don't let navigation lifecycle failures prevent navigation
            }
        }

        // Notify new page it's been navigated to
        if (page is INavigationAware currentAware)
        {
            try
            {
                currentAware.OnNavigatedTo();
            }
            catch
            {
                // Don't let navigation lifecycle failures prevent navigation
            }
        }
    }

    public void Invalidate(Type pageType)
    {
        if (pageType is null)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            RemoveEntry(pageType);
        }
    }

    public bool IsCached(Page? page)
    {
        if (page is null)
        {
            return false;
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            return _entries.Values.Any(entry => ReferenceEquals(entry.Page, page));
        }
    }

    public void SweepExpired()
    {
        lock (_syncRoot)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var type in _entries.Where(kvp => kvp.Value.IsExpired(now)).Select(kvp => kvp.Key).ToArray())
            {
                RemoveEntry(type);
            }
        }
    }

    public DateTimeOffset? GetNextExpirationUtc()
    {
        lock (_syncRoot)
        {
            if (_entries.Count == 0)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? next = null;

            foreach (var entry in _entries.Values)
            {
                if (entry.Policy.IdleExpiration is null)
                {
                    continue; // never expires
                }

                var candidate = entry.LastAccessed + entry.Policy.IdleExpiration.Value;
                if (candidate <= now)
                {
                    continue; // already eligible for sweep
                }

                if (next is null || candidate < next)
                {
                    next = candidate;
                }
            }

            return next;
        }
    }

    public void ClearAll()
    {
        lock (_syncRoot)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            foreach (var entry in _entries.Values)
            {
                entry.Dispose();
            }

            _entries.Clear();
            _currentPage = null;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            foreach (var entry in _entries.Values)
            {
                entry.Dispose();
            }

            _entries.Clear();
            _currentPage = null;
            _isDisposed = true;
        }
    }

    private void RemoveEntry(Type pageType)
    {
        if (_entries.Remove(pageType, out var entry))
        {
            entry.Dispose();
        }
    }

    /// <summary>
    /// Implements LRU-K eviction. Selects the victim page based on backward K-distance.
    /// Pages with larger backward K-distance (less frequently accessed) are evicted first.
    /// For pages that haven't been accessed K times, use infinity as the K-distance,
    /// making them more likely to be evicted (unless they're brand new).
    /// </summary>
    private void TrimIfNeeded()
    {
        if (_entries.Count < MaxEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        Type? victimType = null;
        var maxBackwardKDistance = TimeSpan.MinValue;
        var victimHasFullHistory = false;

        foreach (var kvp in _entries)
        {
            var entry = kvp.Value;

            // Don't evict KeepAlive entries unless we absolutely must
            if (entry.Policy.IdleExpiration is null && victimType is not null)
            {
                continue;
            }

            // Filter out correlated references (multiple accesses within a short period count as one)
            var backwardKDistance = entry.GetBackwardKDistance(now, CorrelatedReferencePeriod);
            var hasFullHistory = entry.HasFullAccessHistory;

            // Prefer evicting entries without full history (accessed fewer than K times)
            // unless current victim also lacks full history - then compare distances
            if (!hasFullHistory && victimHasFullHistory)
            {
                // New candidate lacks full history but current victim has it - prefer evicting new candidate
                victimType = kvp.Key;
                maxBackwardKDistance = backwardKDistance;
                victimHasFullHistory = false;
            }
            else if (hasFullHistory && !victimHasFullHistory && victimType is not null)
            {
                // Current victim lacks full history but new candidate has it - keep current victim
                continue;
            }
            else if (backwardKDistance > maxBackwardKDistance || victimType is null)
            {
                // Same history status - compare K-distances
                victimType = kvp.Key;
                maxBackwardKDistance = backwardKDistance;
                victimHasFullHistory = hasFullHistory;
            }
        }

        if (victimType is not null)
        {
            RemoveEntry(victimType);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SmartPageCache));
        }
    }

    private sealed class CachedPageEntry : IDisposable
    {
        private readonly Queue<DateTimeOffset> _accessHistory;
        private readonly int _historyCapacity;
        private bool _disposed;

        public CachedPageEntry(Page page, PageCachePolicy policy, int historyCapacity)
        {
            Page = page;
            Policy = policy;
            _historyCapacity = historyCapacity;
            _accessHistory = new Queue<DateTimeOffset>(historyCapacity);
            RecordAccess();
        }

        public Page Page { get; }

        public PageCachePolicy Policy { get; }

        public DateTimeOffset LastAccessed { get; private set; }

        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Whether this entry has been accessed at least K times.
        /// </summary>
        public bool HasFullAccessHistory => _accessHistory.Count >= _historyCapacity;

        public bool IsExpired(DateTimeOffset now)
        {
            if (Policy.IdleExpiration is null)
            {
                return false;
            }

            return now - LastAccessed > Policy.IdleExpiration.Value;
        }

        /// <summary>
        /// Records an access, filtering out correlated references.
        /// </summary>
        public void RecordAccess()
        {
            var now = DateTimeOffset.UtcNow;
            LastAccessed = now;

            // Add to history, maintaining capacity
            if (_accessHistory.Count >= _historyCapacity)
            {
                _accessHistory.Dequeue();
            }

            _accessHistory.Enqueue(now);
        }

        /// <summary>
        /// Computes the backward K-distance: time since the K-th most recent access.
        /// Filters out correlated references (accesses within correlatedPeriod of each other).
        /// </summary>
        public TimeSpan GetBackwardKDistance(DateTimeOffset now, TimeSpan correlatedPeriod)
        {
            if (_accessHistory.Count == 0)
            {
                return TimeSpan.MaxValue;
            }

            // Filter correlated references
            var distinctAccesses = new List<DateTimeOffset>();
            DateTimeOffset? lastDistinct = null;

            foreach (var access in _accessHistory.Reverse())
            {
                if (lastDistinct is null || lastDistinct.Value - access > correlatedPeriod)
                {
                    distinctAccesses.Add(access);
                    lastDistinct = access;
                }

                if (distinctAccesses.Count >= _historyCapacity)
                {
                    break;
                }
            }

            // If we don't have K distinct accesses, return a large value
            // (but not MaxValue, to prefer evicting truly new pages over semi-established ones)
            if (distinctAccesses.Count < _historyCapacity)
            {
                // Use time since creation as a proxy, scaled up to indicate less value
                var ageSinceCreation = now - CreatedAt;
                return ageSinceCreation + TimeSpan.FromHours(1);
            }

            // Return time since the K-th most recent distinct access
            var kthAccess = distinctAccesses[^1];
            return now - kthAccess;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (Page.DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }

            if (Page is IDisposable pageDisposable && !ReferenceEquals(pageDisposable, Page.DataContext))
            {
                pageDisposable.Dispose();
            }

            _disposed = true;
        }
    }
}
