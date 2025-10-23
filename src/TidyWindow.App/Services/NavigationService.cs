using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace TidyWindow.App.Services;

/// <summary>
/// Centralizes navigation logic for the shell, using DI to resolve requested pages.
/// </summary>
public sealed class NavigationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<Type, Page> _pageCache = new();
    private Frame? _frame;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public bool IsInitialized => _frame is not null;

    public void Initialize(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _frame.NavigationUIVisibility = NavigationUIVisibility.Hidden;
    }

    public void Navigate(Type pageType)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("Navigation frame has not been initialized yet.");
        }

        if (!typeof(Page).IsAssignableFrom(pageType))
        {
            throw new ArgumentException("Navigation target must derive from Page.", nameof(pageType));
        }

        if (_frame.Content?.GetType() == pageType)
        {
            return;
        }

        if (!_pageCache.TryGetValue(pageType, out var page))
        {
            page = _serviceProvider.GetService(pageType) as Page
                   ?? ActivatorUtilities.CreateInstance(_serviceProvider, pageType) as Page
                   ?? throw new InvalidOperationException($"Unable to resolve page instance for {pageType.FullName}.");

            if (page is ICacheablePage)
            {
                _pageCache[pageType] = page;
            }
        }

        try
        {
            _frame.Navigate(page);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Navigation failure for {pageType.FullName}: {ex}");
            _pageCache.Remove(pageType);
            throw;
        }
    }
}
