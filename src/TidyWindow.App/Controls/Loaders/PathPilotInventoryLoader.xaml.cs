using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace TidyWindow.App.Controls.Loaders;

public partial class PathPilotInventoryLoader : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(PathPilotInventoryLoader),
        new PropertyMetadata(false, OnIsActiveChanged));

    private Storyboard? _spinnerStoryboard;
    private bool _isTemplateLoaded;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public PathPilotInventoryLoader()
    {
        InitializeComponent();
        Loaded += OnLoaderLoaded;
        Unloaded += OnLoaderUnloaded;
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var loader = (PathPilotInventoryLoader)d;
        loader.UpdateVisualState((bool)e.NewValue);
    }

    private void OnLoaderLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaderLoaded;
        _spinnerStoryboard = TryFindResource("InventorySpinnerStoryboard") as Storyboard;
        _isTemplateLoaded = true;
        UpdateVisualState(IsActive, true);
    }

    private void OnLoaderUnloaded(object? sender, RoutedEventArgs e)
    {
        StopAnimations();
        _isTemplateLoaded = false;
        OverlayRoot.Visibility = Visibility.Collapsed;
        OverlayRoot.Opacity = 0;
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
            StartAnimations();
        }
        else if (!isLoadedInvocation)
        {
            StopAnimations();
        }

        var duration = TimeSpan.FromMilliseconds(isActive ? 250 : 180);
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
                }
            };
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
    }
}
