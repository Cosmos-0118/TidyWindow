using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly ActivityLogService _activityLog;
    private readonly SmartPageCache _pageCache;
    private Frame? _frame;
    private readonly Duration _transitionDuration = new(TimeSpan.FromMilliseconds(220));
    private readonly IEasingFunction _transitionEasing = new QuarticEase { EasingMode = EasingMode.EaseInOut };
    private readonly double _transitionOffset = 14d;
    private bool _isTransitioning;
    private Type? _queuedNavigation;
    private Type? _activeNavigationTarget;

    public NavigationService(IServiceProvider serviceProvider, ActivityLogService activityLog, SmartPageCache pageCache)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _pageCache = pageCache ?? throw new ArgumentNullException(nameof(pageCache));
    }

    public bool IsInitialized => _frame is not null;

    public void ClearCache()
    {
        _pageCache.ClearAll();
    }

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
        InitializeFrameTransforms(_frame);
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
            _activityLog.LogInformation("Navigation", $"Navigation skipped; already on {pageType.Name}.");
            return;
        }

        if (_isTransitioning)
        {
            if (_activeNavigationTarget == pageType)
            {
                _activityLog.LogInformation("Navigation", $"Navigation already in progress for {pageType.Name}.");
                return;
            }

            _queuedNavigation = pageType;
            _activityLog.LogInformation("Navigation", $"Navigation queued for {pageType.Name} while transition completes.");
            return;
        }

        _activityLog.LogInformation("Navigation", $"Navigating to {pageType.Name}");

        _pageCache.SweepExpired();
        var page = ResolvePage(pageType);

        BeginTransition(pageType, page);
    }

    private Page ResolvePage(Type pageType)
    {
        if (PageCacheRegistry.TryGetPolicy(pageType, out var policy))
        {
            if (_pageCache.TryGetPage(pageType, out var cached))
            {
                return cached;
            }

            var cachedPage = CreatePageInstance(pageType);
            _pageCache.StorePage(pageType, cachedPage, policy);
            return cachedPage;
        }

        return CreatePageInstance(pageType);
    }

    private Page CreatePageInstance(Type pageType)
    {
        try
        {
            return _serviceProvider.GetService(pageType) as Page
                   ?? ActivatorUtilities.CreateInstance(_serviceProvider, pageType) as Page
                   ?? throw new InvalidOperationException($"Unable to resolve page instance for {pageType.FullName}.");
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Navigation", $"Failed to materialize {pageType.FullName}", new object?[] { ex });
            throw;
        }
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
                var translate = EnsureFrameTranslateTransform();
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = _transitionOffset;
                _frame.Navigate(targetPage);
            }
            catch (Exception ex)
            {
                _activityLog.LogError("Navigation", $"Navigation failure for {pageType.FullName}", new object?[] { ex });
                _pageCache.Invalidate(pageType);
                _frame.Opacity = 1d;
                ResetTransition();
                throw;
            }
        }

        if (_frame.Content is FrameworkElement)
        {
            var fadeOut = CreateAnimation(_frame.Opacity, 0d);
            var translate = EnsureFrameTranslateTransform();
            var slideUp = CreateTranslationAnimation(translate.Y, -_transitionOffset * 0.4);

            void OnFadeOutCompleted(object? sender, EventArgs args)
            {
                fadeOut.Completed -= OnFadeOutCompleted;
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = _transitionOffset;
                NavigateCore();
            }

            fadeOut.Completed += OnFadeOutCompleted;
            _frame.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
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
        var translate = EnsureFrameTranslateTransform();
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        var slideIn = CreateTranslationAnimation(translate.Y, 0d);

        var fadeIn = CreateAnimation(0d, 1d);

        void FadeInCompleted(object? sender, EventArgs args)
        {
            fadeIn.Completed -= FadeInCompleted;
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0d;

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
            else if (_frame.Content is Page currentPage)
            {
                _activityLog.LogInformation("Navigation", $"Navigation complete: {currentPage.GetType().Name}");
            }
        }

        fadeIn.Completed += FadeInCompleted;
        _frame.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        translate.BeginAnimation(TranslateTransform.YProperty, slideIn);
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

    private DoubleAnimation CreateTranslationAnimation(double? from, double to)
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

    private void InitializeFrameTransforms(Frame frame)
    {
        if (frame.RenderTransform is TranslateTransform)
        {
            return;
        }

        if (frame.RenderTransform is TransformGroup group)
        {
            foreach (var transform in group.Children)
            {
                if (transform is TranslateTransform)
                {
                    return;
                }
            }

            group.Children.Add(new TranslateTransform());
            return;
        }

        frame.RenderTransform = new TranslateTransform();
    }

    private TranslateTransform EnsureFrameTranslateTransform()
    {
        if (_frame is null)
        {
            return new TranslateTransform();
        }

        if (_frame.RenderTransform is TranslateTransform translate)
        {
            return translate;
        }

        if (_frame.RenderTransform is TransformGroup group)
        {
            foreach (var transform in group.Children)
            {
                if (transform is TranslateTransform existing)
                {
                    return existing;
                }
            }

            var created = new TranslateTransform();
            group.Children.Add(created);
            return created;
        }

        translate = new TranslateTransform();
        if (_frame.RenderTransform == Transform.Identity)
        {
            _frame.RenderTransform = translate;
            return translate;
        }

        var newGroup = new TransformGroup();
        newGroup.Children.Add(_frame.RenderTransform);
        newGroup.Children.Add(translate);
        _frame.RenderTransform = newGroup;
        return translate;
    }

    private void ResetTransition()
    {
        _isTransitioning = false;
        _activeNavigationTarget = null;
    }
}
