using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TidyWindow.App.Services;

namespace TidyWindow.App.Views.Dialogs;

public partial class PendingAutomationPromptWindow : Window
{
    private const int MaxSummaries = 4;

    public PendingAutomationPromptWindow(IReadOnlyList<AutomationWorkItem> workItems)
    {
        InitializeComponent();
        DataContext = CreateViewModel(workItems);
    }

    public PendingAutomationDecision Result { get; private set; } = PendingAutomationDecision.WaitForCompletion;

    private static PendingAutomationPromptViewModel CreateViewModel(IReadOnlyList<AutomationWorkItem> workItems)
    {
        var summaries = workItems
            .Select(item => item.Description)
            .Where(description => !string.IsNullOrWhiteSpace(description))
            .Take(MaxSummaries)
            .Select(description => description.Trim())
            .ToList();

        var overflow = Math.Max(0, workItems.Count - summaries.Count);

        return new PendingAutomationPromptViewModel(summaries, overflow);
    }

    private void OnCloseAnyway(object sender, RoutedEventArgs e)
    {
        Result = PendingAutomationDecision.CloseAnyway;
        DialogResult = true;
    }

    private void OnWait(object sender, RoutedEventArgs e)
    {
        Result = PendingAutomationDecision.WaitForCompletion;
        DialogResult = true;
    }

    private sealed class PendingAutomationPromptViewModel
    {
        public PendingAutomationPromptViewModel(IReadOnlyList<string> summaries, int overflowCount)
        {
            WorkSummaries = summaries;
            HasOverflow = overflowCount > 0;
            OverflowSummary = HasOverflow ? $"+{overflowCount} more automation task(s)" : string.Empty;
        }

        public IReadOnlyList<string> WorkSummaries { get; }

        public bool HasOverflow { get; }

        public string OverflowSummary { get; }
    }
}
