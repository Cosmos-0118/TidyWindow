using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace TidyWindow.App.Controls.Loaders;

public partial class BootstrapUpdateLoader : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(BootstrapUpdateLoader),
        new PropertyMetadata(false, OnIsActiveChanged));

    private Storyboard? _spinnerStoryboard;
    private bool _isTemplateLoaded;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public BootstrapUpdateLoader()
    {
        InitializeComponent();
        Loaded += OnLoaderLoaded;
        Unloaded += OnLoaderUnloaded;
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var loader = (BootstrapUpdateLoader)d;
        loader.UpdateVisualState((bool)e.NewValue);
    }

    private void OnLoaderLoaded(object sender, RoutedEventArgs e)
    {
        _spinnerStoryboard ??= TryFindResource("LoaderSpinnerStoryboard") as Storyboard;
        _isTemplateLoaded = true;
        UpdateVisualState(IsActive, true);
    }

    private void OnLoaderUnloaded(object? sender, RoutedEventArgs e)
    {
        StopAnimations();
        _isTemplateLoaded = false;
        if (OverlayRoot != null)
        {
            OverlayRoot.Visibility = Visibility.Collapsed;
            OverlayRoot.Opacity = 0;
        }
    }

    private void UpdateVisualState(bool isActive, bool isLoadedInvocation = false)
    {
        if (!_isTemplateLoaded && !isLoadedInvocation)
        {
            return;
        }

        if (OverlayRoot == null)
        {
            return;
        }

        if (isActive)
        {
            OverlayRoot.Visibility = Visibility.Visible;
            OverlayRoot.IsHitTestVisible = true;
            StartAnimations();
        }
        else if (!isLoadedInvocation)
        {
            StopAnimations();
            OverlayRoot.IsHitTestVisible = true;
        }

        var duration = TimeSpan.FromMilliseconds(isActive ? 260 : 180);
        var fadeAnimation = new DoubleAnimation(isActive ? 1 : 0, duration)
        {
            EasingFunction = new QuadraticEase()
        };

        if (!isActive)
        {
            fadeAnimation.Completed += (_, _) =>
            {
                if (!IsActive)
                {
                    OverlayRoot.Visibility = Visibility.Collapsed;
                    OverlayRoot.IsHitTestVisible = false;
                }
            };
        }
        else
        {
            OverlayRoot.IsHitTestVisible = true;
        }

        OverlayRoot.BeginAnimation(OpacityProperty, fadeAnimation);
    }

    private void StartAnimations()
    {
        _spinnerStoryboard?.Begin(OverlayRoot, true);
    }

    private void StopAnimations()
    {
        _spinnerStoryboard?.Stop(OverlayRoot);
        if (OverlayRoot != null)
        {
            OverlayRoot.IsHitTestVisible = false;
        }
    }
}
