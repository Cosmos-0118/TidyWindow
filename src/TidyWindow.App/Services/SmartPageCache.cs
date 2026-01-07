using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace TidyWindow.App.Services;

/// <summary>
/// Provides simple lifetime-aware caching for navigation pages.
/// </summary>
public sealed class SmartPageCache : IDisposable
{
    private readonly Dictionary<Type, CachedPageEntry> _entries = new();
    private readonly object _syncRoot = new();
    private bool _isDisposed;

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

            entry.Touch();
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
            _entries[pageType] = new CachedPageEntry(page, policy);
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

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SmartPageCache));
        }
    }

    private sealed class CachedPageEntry : IDisposable
    {
        private bool _disposed;

        public CachedPageEntry(Page page, PageCachePolicy policy)
        {
            Page = page;
            Policy = policy;
            Touch();
        }

        public Page Page { get; }

        public PageCachePolicy Policy { get; }

        public DateTimeOffset LastTouched { get; private set; }

        public bool IsExpired(DateTimeOffset now)
        {
            if (Policy.IdleExpiration is null)
            {
                return false;
            }

            return now - LastTouched > Policy.IdleExpiration.Value;
        }

        public void Touch()
        {
            LastTouched = DateTimeOffset.UtcNow;
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
