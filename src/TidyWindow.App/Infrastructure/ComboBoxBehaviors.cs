using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;

namespace TidyWindow.App.Infrastructure;

/// <summary>
/// Attached helpers that smooth out ComboBox UX, including keeping the popup anchored during scroll.
/// </summary>
public static class ComboBoxBehaviors
{
    public static readonly DependencyProperty EnableStickyPopupProperty =
        DependencyProperty.RegisterAttached(
            "EnableStickyPopup",
            typeof(bool),
            typeof(ComboBoxBehaviors),
            new PropertyMetadata(false, OnEnableStickyPopupChanged));

    public static bool GetEnableStickyPopup(DependencyObject obj)
    {
        return obj is not null && (bool)obj.GetValue(EnableStickyPopupProperty);
    }

    public static void SetEnableStickyPopup(DependencyObject obj, bool value)
    {
        obj?.SetValue(EnableStickyPopupProperty, value);
    }

    private static readonly DependencyProperty TrackerProperty =
        DependencyProperty.RegisterAttached(
            "_ComboBoxPopupTracker",
            typeof(ComboBoxPopupTracker),
            typeof(ComboBoxBehaviors));

    private static void OnEnableStickyPopupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo)
        {
            return;
        }

        if (Equals(e.NewValue, true))
        {
            combo.Loaded += OnComboLoaded;
            combo.Unloaded += OnComboUnloaded;
            if (combo.IsLoaded)
            {
                AttachTracker(combo);
            }
        }
        else
        {
            combo.Loaded -= OnComboLoaded;
            combo.Unloaded -= OnComboUnloaded;
            DetachTracker(combo);
        }
    }

    private static void OnComboLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            AttachTracker(combo);
        }
    }

    private static void OnComboUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            DetachTracker(combo);
        }
    }

    private static void AttachTracker(ComboBox combo)
    {
        if (combo.GetValue(TrackerProperty) is ComboBoxPopupTracker)
        {
            return;
        }

        var tracker = new ComboBoxPopupTracker(combo);
        combo.SetValue(TrackerProperty, tracker);
        tracker.Attach();
    }

    private static void DetachTracker(ComboBox combo)
    {
        if (combo.GetValue(TrackerProperty) is ComboBoxPopupTracker tracker)
        {
            tracker.Detach();
            combo.ClearValue(TrackerProperty);
        }
    }

    private sealed class ComboBoxPopupTracker
    {
        private readonly ComboBox _combo;
        private ScrollViewer? _scrollViewer;
        private Popup? _popup;

        public ComboBoxPopupTracker(ComboBox combo)
        {
            _combo = combo ?? throw new ArgumentNullException(nameof(combo));
        }

        public void Attach()
        {
            _combo.DropDownOpened += OnDropDownOpened;
            _combo.DropDownClosed += OnDropDownClosed;
            HookScrollViewer();
        }

        public void Detach()
        {
            _combo.DropDownOpened -= OnDropDownOpened;
            _combo.DropDownClosed -= OnDropDownClosed;
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer = null;
            }

            _popup = null;
        }

        private void HookScrollViewer()
        {
            _scrollViewer ??= FindAncestorScrollViewer(_combo);
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer.ScrollChanged += OnScrollChanged;
            }
        }

        private void OnDropDownOpened(object? sender, EventArgs e)
        {
            EnsurePopup();
        }

        private void OnDropDownClosed(object? sender, EventArgs e)
        {
            // No-op; keeping popup reference for next open reduces template lookups.
        }

        private void EnsurePopup()
        {
            if (_popup is not null)
            {
                return;
            }

            _combo.ApplyTemplate();
            _popup = _combo.Template.FindName("PART_Popup", _combo) as Popup;
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (!_combo.IsDropDownOpen)
            {
                return;
            }

            var popup = _popup;
            if (popup is null)
            {
                return;
            }

            // Nudging HorizontalOffset forces WPF to recompute popup position without visual jank.
            var offset = popup.HorizontalOffset;
            popup.HorizontalOffset = offset + 0.1;
            popup.HorizontalOffset = offset;
        }

        private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? start)
        {
            while (start is not null)
            {
                if (start is ScrollViewer viewer)
                {
                    return viewer;
                }

                start = VisualTreeHelper.GetParent(start);
            }

            return null;
        }
    }
}
