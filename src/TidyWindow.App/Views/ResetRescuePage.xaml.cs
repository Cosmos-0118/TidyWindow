using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using Forms = System.Windows.Forms;

namespace TidyWindow.App.Views;

public partial class ResetRescuePage : Page, INavigationAware
{
    private readonly ResetRescueViewModel _viewModel;

    public ResetRescuePage(ResetRescueViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
    }

    private void OnBrowseDestination(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            SelectedPath = _viewModel.DestinationPath,
            ShowNewFolderButton = true,
            Description = "Select a folder to save the Reset Rescue archive"
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            _viewModel.DestinationPath = dialog.SelectedPath;
        }
    }

    private void OnBrowseArchive(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Reset Rescue archive (*.zip;*.rrarchive)|*.zip;*.rrarchive|All files (*.*)|*.*",
            FileName = _viewModel.RestoreArchivePath
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.RestoreArchivePath = dialog.FileName;
        }
    }

    private void OnChooseSources(object sender, RoutedEventArgs e)
    {
        var selected = new List<string>();

        var folderDialog = new OpenFolderDialog
        {
            Title = "Select folders to include",
            Multiselect = true,
            InitialDirectory = ResolveInitialExplorerDirectory()
        };

        if (folderDialog.ShowDialog() == true)
        {
            selected.AddRange(folderDialog.FolderNames.Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        var fileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select files to include",
            Filter = "All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            InitialDirectory = ResolveInitialExplorerDirectory()
        };

        if (fileDialog.ShowDialog() == true)
        {
            selected.AddRange(fileDialog.FileNames.Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        if (selected.Count > 0)
        {
            _viewModel.AddExplorerSources(selected);
        }
    }

    private string ResolveInitialExplorerDirectory()
    {
        var destination = _viewModel.DestinationPath;
        if (!string.IsNullOrWhiteSpace(destination))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(destination);
                if (Directory.Exists(expanded))
                {
                    return expanded;
                }

                var directory = Path.GetDirectoryName(expanded);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }
            catch
            {
                // ignored
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        // No special handling needed
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        // No special handling needed
    }
}
