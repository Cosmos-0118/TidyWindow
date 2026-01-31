using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class LogsPage : Page
{
    private readonly LogsViewModel _viewModel;
    private bool _isDisposed;
    private readonly bool _shouldDisposeOnUnload;
    private ActivityLogItemViewModel? _currentDetailEntry;

    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _shouldDisposeOnUnload = !PageCacheRegistry.IsCacheable(GetType());
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>
    /// Handles mouse wheel events on the ListView and bubbles them to the parent ScrollViewer.
    /// This fixes scroll issues on cached pages where the ListView captures mouse wheel events.
    /// </summary>
    private void OnListViewPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        // Bubble the event to the parent ScrollViewer
        e.Handled = true;
        var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender
        };
        LeftScrollViewer?.RaiseEvent(eventArg);
    }

    private void OnViewDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is ActivityLogItemViewModel entry)
        {
            ShowDetailsPopup(entry);
        }
    }

    private void ShowDetailsPopup(ActivityLogItemViewModel entry)
    {
        _currentDetailEntry = entry;

        // Populate the popup
        DetailLevel.Text = entry.LevelDisplay;
        DetailTime.Text = entry.TimestampDisplay;
        DetailSource.Text = entry.Source;
        DetailMessage.Text = entry.Message;

        // Populate details list
        DetailsList.Children.Clear();
        if (entry.Details != null && entry.Details.Count > 0)
        {
            foreach (var detail in entry.Details)
            {
                var textBlock = new TextBlock
                {
                    Text = detail,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#94A3B8")),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                DetailsList.Children.Add(textBlock);
            }
            NoDetailsText.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoDetailsText.Visibility = Visibility.Visible;
        }

        // Show overlay
        DetailsOverlay.Visibility = Visibility.Visible;
        RightScrollViewer?.ScrollToTop();
    }

    private void OnCloseDetailsClick(object sender, RoutedEventArgs e)
    {
        CloseDetailsPopup();
    }

    private void OnOverlayBackdropClick(object sender, MouseButtonEventArgs e)
    {
        CloseDetailsPopup();
    }

    private void CloseDetailsPopup()
    {
        DetailsOverlay.Visibility = Visibility.Collapsed;
        _currentDetailEntry = null;
    }

    private void OnCopyDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_currentDetailEntry != null)
        {
            _viewModel.CopyEntryCommand.Execute(_currentDetailEntry);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResetScrollPosition();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ResetScrollPosition();
        }
    }

    private void ResetScrollPosition()
    {
        // Reset the scroll position to top when the page becomes visible
        // This fixes scroll issues when navigating back to cached pages
        LeftScrollViewer?.ScrollToTop();
        RightScrollViewer?.ScrollToTop();
        CloseDetailsPopup();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed || !_shouldDisposeOnUnload)
        {
            return;
        }

        Unloaded -= OnUnloaded;
        _viewModel.Dispose();
        _isDisposed = true;
    }
}
