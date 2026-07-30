using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

/// <summary>
/// Modal dialog window that edits a shell entity's command line, working directory, arguments,
/// and environment variables. Mirrors <see cref="SettingsDialogWindow"/>'s ShowDialog pattern.
/// </summary>
public partial class ShellSettingsDialogWindow : Window
{
    private readonly ShellSettingsDialogViewModel? viewModel;

    /// <summary>Design-time constructor.</summary>
    public ShellSettingsDialogWindow()
    {
        this.InitializeComponent();
    }

    /// <summary>Creates the dialog for the supplied view model.</summary>
    public ShellSettingsDialogWindow(ShellSettingsDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        this.viewModel = viewModel;
        this.InitializeComponent();
        this.DataContext = viewModel;
    }

    /// <summary>Whether Save was clicked and completed.</summary>
    public bool Saved { get; private set; }

    /// <summary>The updated spec produced by Save.</summary>
    public ShellEntityOpenSpec? Result { get; private set; }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        this.Result = await this.viewModel.SaveAsync();
        this.Saved = true;
        this.Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
