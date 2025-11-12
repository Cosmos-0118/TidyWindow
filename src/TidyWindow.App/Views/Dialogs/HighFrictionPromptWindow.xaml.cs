using System;
using System.Windows;
using System.Windows.Input;
using TidyWindow.App.Services;

namespace TidyWindow.App.Views.Dialogs;

public partial class HighFrictionPromptWindow : Window
{
    public HighFrictionPromptWindow(string title, string message, string suggestion)
    {
        InitializeComponent();
        HeadingTextBlock.Text = title;
        BodyTextBlock.Text = message;
        SuggestionTextBlock.Text = suggestion;
        Result = HighFrictionPromptResult.Dismissed;
    }

    public HighFrictionPromptResult Result { get; private set; }

    private void OnViewLogs(object sender, RoutedEventArgs e)
    {
        Result = HighFrictionPromptResult.ViewLogs;
        DialogResult = true;
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        Result = HighFrictionPromptResult.RestartApp;
        DialogResult = true;
    }

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        Result = HighFrictionPromptResult.Dismissed;
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (!DialogResult.HasValue)
        {
            Result = HighFrictionPromptResult.Dismissed;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Result = HighFrictionPromptResult.Dismissed;
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Ignore if DragMove is invoked during closing animations.
            }
        }
    }
}
