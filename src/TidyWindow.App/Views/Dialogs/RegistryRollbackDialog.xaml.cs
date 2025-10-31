using System;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using TidyWindow.App.Resources.Strings;

namespace TidyWindow.App.Views.Dialogs;

public partial class RegistryRollbackDialog : Window
{
    private readonly DispatcherTimer _timer;
    private int _remainingSeconds;

    public RegistryRollbackDialog(int countdownSeconds = 30)
    {
        InitializeComponent();

        if (countdownSeconds < 5)
        {
            countdownSeconds = 5;
        }

        _remainingSeconds = countdownSeconds;
        UpdateCountdownText();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public bool ShouldRevert { get; private set; }

    public bool WasAutoTriggered { get; private set; }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_remainingSeconds <= 0)
        {
            return;
        }

        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            _timer.Stop();
            CountdownTextBlock.Text = RegistryOptimizerStrings.RollbackDialogAutoNotice;
            WasAutoTriggered = true;
            ShouldRevert = true;
            DialogResult = false;
            return;
        }

        UpdateCountdownText();
    }

    private void KeepButton_Click(object sender, RoutedEventArgs e)
    {
        ShouldRevert = false;
        WasAutoTriggered = false;
        DialogResult = true;
    }

    private void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        ShouldRevert = true;
        WasAutoTriggered = false;
        DialogResult = false;
    }

    private void UpdateCountdownText()
    {
        CountdownTextBlock.Text = string.Format(CultureInfo.CurrentCulture, RegistryOptimizerStrings.RollbackDialogCountdown, _remainingSeconds);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        base.OnClosed(e);
    }
}
