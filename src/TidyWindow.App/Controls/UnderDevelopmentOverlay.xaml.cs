namespace TidyWindow.App.Controls;

/// <summary>
/// A simple overlay that blocks interaction and displays an "Under Development" message.
/// 
/// USAGE:
/// ------
/// To add to a page, wrap your content in a Grid and add this as the last child:
///   <Grid>
///       <!-- Your existing page content here -->
///       <ux:UnderDevelopmentOverlay />
///   </Grid>
/// 
/// TO REMOVE:
/// ----------
/// Simply delete or comment out the UnderDevelopmentOverlay tag from the XAML.
/// </summary>
public partial class UnderDevelopmentOverlay
{
    public UnderDevelopmentOverlay()
    {
        InitializeComponent();
    }
}
