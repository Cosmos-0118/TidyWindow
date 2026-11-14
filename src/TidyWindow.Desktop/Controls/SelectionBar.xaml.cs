using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace TidyWindow.Next.Desktop;

/// <summary>The strip above a list the person can tick things in: the "select all" tick, how many are ticked, Clear, and the actions for the ticked things.</summary>
public sealed partial class SelectionBar : UserControl
{
    public static readonly DependencyProperty IsAllSelectedProperty = Register(nameof(IsAllSelected), typeof(object), false);
    public static readonly DependencyProperty CanSelectProperty = Register(nameof(CanSelect), typeof(bool), true);
    public static readonly DependencyProperty ShowSelectAllProperty = Register(nameof(ShowSelectAll), typeof(bool), true);
    public static readonly DependencyProperty SelectAllLabelProperty = Register(nameof(SelectAllLabel), typeof(string), "Select all");
    public static readonly DependencyProperty CountProperty = Register(nameof(Count), typeof(int), 0);
    public static readonly DependencyProperty HiddenCountProperty = Register(nameof(HiddenCount), typeof(int), 0);
    public static readonly DependencyProperty ActionsProperty = Register(nameof(Actions), typeof(object), null);
    public static readonly DependencyProperty ActionsAlwaysShownProperty = Register(nameof(ActionsAlwaysShown), typeof(bool), false);

    private static DependencyProperty Register(string name, Type type, object? value) =>
        DependencyProperty.Register(name, type, typeof(SelectionBar), new PropertyMetadata(value, (sender, _) => ((SelectionBar)sender).Refresh()));

    /// <summary>All, some or none of what can be ticked is ticked (null is "some").</summary>
    public bool? IsAllSelected { get => GetValue(IsAllSelectedProperty) as bool?; set => SetValue(IsAllSelectedProperty, value); }
    public bool CanSelect { get => (bool)GetValue(CanSelectProperty); set => SetValue(CanSelectProperty, value); }
    /// <summary>False on a page where ticking everything at once would not make sense: the bar then only counts and clears.</summary>
    public bool ShowSelectAll { get => (bool)GetValue(ShowSelectAllProperty); set => SetValue(ShowSelectAllProperty, value); }
    public string SelectAllLabel { get => (string)GetValue(SelectAllLabelProperty); set => SetValue(SelectAllLabelProperty, value); }
    /// <summary>How many are ticked, including any a search is hiding.</summary>
    public int Count { get => (int)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    /// <summary>How many of the ticked ones a search or filter is hiding right now.</summary>
    public int HiddenCount { get => (int)GetValue(HiddenCountProperty); set => SetValue(HiddenCountProperty, value); }
    /// <summary>The buttons that work on the ticked things. They show only while something is ticked, unless <see cref="ActionsAlwaysShown"/>.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public bool ActionsAlwaysShown { get => (bool)GetValue(ActionsAlwaysShownProperty); set => SetValue(ActionsAlwaysShownProperty, value); }

    public event RoutedEventHandler? SelectAllClick;
    public event RoutedEventHandler? ClearClick;

    public SelectionBar()
    {
        InitializeComponent();
        Refresh();
    }

    private void MasterClick(object sender, RoutedEventArgs e)
    {
        SelectAllClick?.Invoke(this, e);
        // The tick turned itself over when it was clicked; show what the page says instead.
        Master.IsChecked = IsAllSelected;
    }

    private void LabelTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Count > 0 || !CanSelect || !ShowSelectAll) return;
        SelectAllClick?.Invoke(this, e);
        Master.IsChecked = IsAllSelected;
    }

    private void ClearClicked(object sender, RoutedEventArgs e) => ClearClick?.Invoke(this, e);

    private void BarResized(object sender, SizeChangedEventArgs e) => Arrange();

    /// <summary>On a narrow window the actions drop under the count instead of squeezing it.</summary>
    private void Arrange()
    {
        if (Layout is null) return;
        var narrow = ActualWidth > 0 && ActualWidth < 640;
        Grid.SetRow(ActionsHost, narrow ? 1 : 0);
        Grid.SetColumn(ActionsHost, narrow ? 0 : 4);
        Grid.SetColumnSpan(ActionsHost, narrow ? 5 : 1);
        ActionsHost.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Layout.RowSpacing = narrow ? 8 : 0;
        Frame.Padding = narrow ? new Thickness(12, 8, 12, 8) : new Thickness(12, 0, 12, 0);
    }

    private void Refresh()
    {
        if (Frame is null) return;
        var selected = Count > 0;
        Master.Visibility = ShowSelectAll ? Visibility.Visible : Visibility.Collapsed;
        Master.IsEnabled = CanSelect;
        Master.IsChecked = IsAllSelected;
        AutomationProperties.SetName(Master, SelectAllLabel);
        CountLabel.Text = $"{Count:N0} selected";
        CountLabel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        RestLabel.Text = ShowSelectAll ? SelectAllLabel : "Nothing selected";
        RestLabel.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        RestLabel.Opacity = ShowSelectAll && !CanSelect ? 0.6 : 1;
        HiddenNote.Visibility = selected && HiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenText.Text = $"{HiddenCount:N0} hidden";
        ToolTipService.SetToolTip(HiddenNote, "Ticked, but hidden by the search or filter. Anything you do here still includes them.");
        ClearGroup.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        ActionsHost.Content = Actions;
        ActionsHost.Visibility = selected || ActionsAlwaysShown ? Visibility.Visible : Visibility.Collapsed;
        Fill.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        Arrange();
    }
}
