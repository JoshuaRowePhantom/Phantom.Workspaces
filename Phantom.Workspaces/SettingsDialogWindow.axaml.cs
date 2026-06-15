using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces;

/// <summary>
/// Settings dialog window bound to the shared <see cref="WorkspacesSettingsViewModel"/>.
/// </summary>
public partial class SettingsDialogWindow : Window
{
    private readonly WorkspacesSettingsViewModel? viewModel;

    /// <summary>Design-time constructor.</summary>
    public SettingsDialogWindow()
    {
        this.InitializeComponent();
    }

    /// <summary>Creates the settings dialog for the supplied settings view model.</summary>
    public SettingsDialogWindow(WorkspacesSettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        this.viewModel = viewModel;
        this.InitializeComponent();
        this.DataContext = viewModel;
    }

    /// <summary>Whether the settings were saved.</summary>
    public bool Saved { get; private set; }

    /// <summary>The configuration produced when settings were saved, if any.</summary>
    public WorkspacesConfiguration? Result { get; private set; }

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
