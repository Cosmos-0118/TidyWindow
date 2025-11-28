using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.ViewModels.Dialogs;

public sealed partial class ProcessQuestionnaireDialogViewModel : ObservableObject
{
    public ProcessQuestionnaireDialogViewModel(ProcessQuestionnaireDefinition definition, ProcessQuestionnaireSnapshot snapshot)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        var answers = snapshot?.Answers ?? ProcessQuestionnaireSnapshot.Empty.Answers;
        Questions = new ObservableCollection<ProcessQuestionDialogQuestionViewModel>();
        foreach (var question in definition.Questions)
        {
            var selected = answers.TryGetValue(question.Id, out var value) ? value : null;
            Questions.Add(new ProcessQuestionDialogQuestionViewModel(question, selected));
        }
    }

    public ObservableCollection<ProcessQuestionDialogQuestionViewModel> Questions { get; }

    public IDictionary<string, string>? Answers { get; private set; }

    public bool TryCommitAnswers(out string? error)
    {
        error = null;
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var question in Questions)
        {
            if (string.IsNullOrWhiteSpace(question.SelectedOptionId))
            {
                if (question.Required)
                {
                    error = $"Select an option for \"{question.Title}\".";
                    return false;
                }

                continue;
            }

            results[question.Id] = question.SelectedOptionId!;
        }

        Answers = results;
        return true;
    }
}

public sealed partial class ProcessQuestionDialogQuestionViewModel : ObservableObject
{
    public ProcessQuestionDialogQuestionViewModel(ProcessQuestion question, string? selectedOptionId)
    {
        if (question is null)
        {
            throw new ArgumentNullException(nameof(question));
        }

        Id = question.Id;
        Title = question.Title;
        Prompt = question.Prompt;
        Required = question.Required;
        Options = new ObservableCollection<ProcessQuestionDialogOptionViewModel>();

        foreach (var option in question.Options)
        {
            Options.Add(new ProcessQuestionDialogOptionViewModel(option));
        }

        SelectedOptionId = selectedOptionId;
    }

    public string Id { get; }

    public string Title { get; }

    public string Prompt { get; }

    public bool Required { get; }

    public ObservableCollection<ProcessQuestionDialogOptionViewModel> Options { get; }

    [ObservableProperty]
    private string? _selectedOptionId;
}

public sealed class ProcessQuestionDialogOptionViewModel
{
    public ProcessQuestionDialogOptionViewModel(ProcessQuestionOption option)
    {
        if (option is null)
        {
            throw new ArgumentNullException(nameof(option));
        }

        Id = option.Id;
        Label = option.Label;
        Description = option.Description;
    }

    public string Id { get; }

    public string Label { get; }

    public string? Description { get; }
}
