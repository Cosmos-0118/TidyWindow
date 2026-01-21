using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TidyWindow.App.Views;

public partial class SplashScreenWindow : Window
{
    private readonly TaskCompletionSource<bool> _closeCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closeRequested;

    public static readonly DependencyProperty StatusMessageProperty = DependencyProperty.Register(
        nameof(StatusMessage),
        typeof(string),
        typeof(SplashScreenWindow),
        new PropertyMetadata("Preparing workspace..."));

    public string StatusMessage
    {
        get => (string)GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    public SplashScreenWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _closeCompletionSource.TrySetResult(true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Choose an animation profile based on WPF render tier.
        // Tier 0/1 tends to indicate software rendering / weaker GPU where multiple simultaneous animations can stutter.
        var tier = RenderCapability.Tier >> 16;

        if (tier >= 2)
        {
            StartStoryboard("PulseStoryboard");
            StartStoryboard("ProgressStoryboard");
            return;
        }

        StartStoryboard("LowPowerPulseStoryboard");
    }

    private void StartStoryboard(string resourceKey)
    {
        if (TryFindResource(resourceKey) is not Storyboard template)
        {
            return;
        }

        var storyboard = template.Clone();
        storyboard.Begin(this, true);
    }

    public void UpdateStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        void SetAndAnimate()
        {
            StatusMessage = message;
            if (TryFindResource("StatusMessageTransitionStoryboard") is Storyboard template)
            {
                var storyboard = template.Clone();
                storyboard.Begin(this, true);
            }
        }

        if (Dispatcher.CheckAccess())
        {
            SetAndAnimate();
        }
        else
        {
            Dispatcher.Invoke(SetAndAnimate);
        }
    }

    public async Task CloseWithFadeAsync(TimeSpan? delay = null)
    {
        if (_closeRequested)
        {
            await _closeCompletionSource.Task.ConfigureAwait(false);
            return;
        }

        _closeRequested = true;

        if (delay.HasValue && delay.Value > TimeSpan.Zero)
        {
            await Task.Delay(delay.Value).ConfigureAwait(false);
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (TryFindResource("SplashFadeOutStoryboard") is not Storyboard template)
            {
                Close();
                return;
            }

            var storyboard = template.Clone();
            void OnCompleted(object? _, EventArgs __)
            {
                storyboard.Completed -= OnCompleted;
                storyboard.Remove(this);
                Close();
            }

            storyboard.Completed += OnCompleted;
            storyboard.Begin(this, true);
        }).Task.ConfigureAwait(false);

        await _closeCompletionSource.Task.ConfigureAwait(false);
    }
}
