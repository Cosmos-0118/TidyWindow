using System;
using System.Windows;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

public partial class CleanupCelebrationView : UserControl
{
    public CleanupCelebrationView()
    {
        InitializeComponent();
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        RestartAnimation();
    }

    public void RestartAnimation()
    {
        if (this.Resources["CelebrateStoryboard"] is not Storyboard storyboard)
        {
            return;
        }

        storyboard.Stop(this);
        storyboard.Begin(this, true);
    }
}
