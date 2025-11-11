using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
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
    private readonly Duration _transitionDuration = new(TimeSpan.FromMilliseconds(220));
    private readonly IEasingFunction _transitionEasing = new QuarticEase { EasingMode = EasingMode.EaseInOut };
    private bool _isTransitioning;
    private Type? _queuedNavigation;
    private Type? _activeNavigationTarget;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public bool IsInitialized => _frame is not null;

    public void Initialize(Frame frame)
    {
        if (_frame is not null)
        {
            _frame.Navigated -= OnFrameNavigated;
        }

        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _frame.NavigationUIVisibility = NavigationUIVisibility.Hidden;
        _frame.Navigated += OnFrameNavigated;
        _frame.Opacity = 1d;
    }

    public void Navigate(Type pageType)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("Navigation frame has not been initialized yet.");
        }

        if (pageType is null)
        {
            throw new ArgumentNullException(nameof(pageType));
        }

        if (!typeof(Page).IsAssignableFrom(pageType))
        {
            throw new ArgumentException("Navigation target must derive from Page.", nameof(pageType));
        }

        if (_frame.Content?.GetType() == pageType)
        {
            return;
        }

        if (_isTransitioning)
        {
            if (_activeNavigationTarget == pageType)
            {
                return;
            }

            _queuedNavigation = pageType;
            return;
        }

        var page = ResolvePage(pageType);

        BeginTransition(pageType, page);
    }

    private Page ResolvePage(Type pageType)
    {
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

        return page;
    }

    private void BeginTransition(Type pageType, Page targetPage)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("Navigation frame has not been initialized yet.");
        }

        _isTransitioning = true;
        _activeNavigationTarget = pageType;

        void NavigateCore()
        {
            if (_frame is null)
            {
                ResetTransition();
                return;
            }

            try
            {
                _frame.BeginAnimation(UIElement.OpacityProperty, null);
                _frame.Opacity = 0d;
                _frame.Navigate(targetPage);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Navigation failure for {pageType.FullName}: {ex}");
                _pageCache.Remove(pageType);
                _frame.Opacity = 1d;
                ResetTransition();
                throw;
            }
        }

        if (_frame.Content is FrameworkElement)
        {
            var fadeOut = CreateAnimation(_frame.Opacity, 0d);

            void OnFadeOutCompleted(object? sender, EventArgs args)
            {
                fadeOut.Completed -= OnFadeOutCompleted;
                NavigateCore();
            }

            fadeOut.Completed += OnFadeOutCompleted;
            _frame.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
        else
        {
            NavigateCore();
        }
    }

    private void OnFrameNavigated(object? sender, NavigationEventArgs e)
    {
        if (_frame is null)
        {
            return;
        }

        _frame.BeginAnimation(UIElement.OpacityProperty, null);
        _frame.Opacity = 0d;

        var fadeIn = CreateAnimation(0d, 1d);

        void FadeInCompleted(object? sender, EventArgs args)
        {
            fadeIn.Completed -= FadeInCompleted;

            var pending = _queuedNavigation;
            _queuedNavigation = null;
            ResetTransition();

            if (pending is not null)
            {
                var currentType = _frame.Content?.GetType();
                if (currentType != pending)
                {
                    Navigate(pending);
                }
            }
        }

        fadeIn.Completed += FadeInCompleted;
        _frame.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private DoubleAnimation CreateAnimation(double? from, double to)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = _transitionDuration,
            EasingFunction = _transitionEasing
        };

        if (from.HasValue)
        {
            animation.From = from.Value;
        }

        return animation;
    }

    private void ResetTransition()
    {
        _isTransitioning = false;
        _activeNavigationTarget = null;
    }
}
