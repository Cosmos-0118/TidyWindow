using System;
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
