using System;
using System.Diagnostics;
using System.Windows.Navigation;

namespace TidyWindow.App.Views.DriverUpdates;

public partial class DriverUpdatesInsightsView : System.Windows.Controls.UserControl
{
    public DriverUpdatesInsightsView()
    {
        InitializeComponent();
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // best-effort navigation only
        }

        e.Handled = true;
    }
}
