using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWindow.App.ViewModels.Preview;

namespace TidyWindow.App.ViewModels.Filters;

public sealed partial class CleanupExtensionFilterModel : ObservableObject
{
    private readonly HashSet<string> _activeExtensions = new(StringComparer.OrdinalIgnoreCase);

    public CleanupExtensionFilterModel(
        IEnumerable<CleanupExtensionFilterOption> filterOptions,
        IEnumerable<CleanupExtensionProfile> profiles)
    {
        if (filterOptions is null)
        {
            throw new ArgumentNullException(nameof(filterOptions));
        }

        if (profiles is null)
        {
            throw new ArgumentNullException(nameof(profiles));
        }

        FilterOptions = filterOptions.ToList();
        Profiles = profiles.ToList();
        SelectedProfile = Profiles.FirstOrDefault();
        RebuildActiveExtensions();
    }

    public IReadOnlyList<CleanupExtensionFilterOption> FilterOptions { get; }

    public IReadOnlyList<CleanupExtensionProfile> Profiles { get; }

    [ObservableProperty]
    private CleanupExtensionFilterMode _mode = CleanupExtensionFilterMode.None;

    [ObservableProperty]
    private CleanupExtensionProfile? _selectedProfile;

    [ObservableProperty]
    private string _customInput = string.Empty;

    public IReadOnlyCollection<string> ActiveExtensions => _activeExtensions;

    public bool IsSelectorEnabled => Mode != CleanupExtensionFilterMode.None;

    public string ExtensionStatusText
    {
        get
        {
            if (Mode == CleanupExtensionFilterMode.None)
            {
                return "Extension filter disabled.";
            }

            if (_activeExtensions.Count == 0)
            {
                return "No extensions configured yet.";
            }

            var verb = Mode == CleanupExtensionFilterMode.IncludeOnly ? "Including" : "Excluding";
            var formatted = string.Join(", ", _activeExtensions.OrderBy(static x => x));
            return $"{verb} {formatted}";
        }
    }

    public event EventHandler? FilterChanged;

    partial void OnModeChanged(CleanupExtensionFilterMode value)
    {
        OnPropertyChanged(nameof(IsSelectorEnabled));
        RebuildActiveExtensions();
    }

    partial void OnSelectedProfileChanged(CleanupExtensionProfile? value)
    {
        RebuildActiveExtensions();
    }

    partial void OnCustomInputChanged(string value)
    {
        RebuildActiveExtensions();
    }

    private void RebuildActiveExtensions()
    {
        _activeExtensions.Clear();

        if (Mode != CleanupExtensionFilterMode.None)
        {
            if (SelectedProfile?.Extensions is { } presetExtensions)
            {
                foreach (var preset in presetExtensions)
                {
                    AddIfValid(preset);
                }
            }

            foreach (var entry in ParseExtensions(CustomInput))
            {
                AddIfValid(entry);
            }
        }

        FilterChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(ExtensionStatusText));
        OnPropertyChanged(nameof(ActiveExtensions));
    }

    private void AddIfValid(string? value)
    {
        var normalized = CleanupPreviewFilter.NormalizeExtension(value);
        if (!string.IsNullOrEmpty(normalized))
        {
            _activeExtensions.Add(normalized);
        }
    }

    private static IEnumerable<string> ParseExtensions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var tokens = value.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            yield return token;
        }
    }
}
