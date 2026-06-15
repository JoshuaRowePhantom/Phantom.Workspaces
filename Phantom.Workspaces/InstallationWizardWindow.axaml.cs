using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces;

/// <summary>
/// First-run installation wizard window bound to the shared <see cref="WorkspacesSettingsViewModel"/>.
/// </summary>
public partial class InstallationWizardWindow : Window
{
    private readonly WorkspacesSettingsViewModel? viewModel;

    /// <summary>Design-time constructor.</summary>
    public InstallationWizardWindow()
    {
        this.InitializeComponent();
    }

    /// <summary>Creates the wizard window for the supplied settings view model.</summary>
    public InstallationWizardWindow(WorkspacesSettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        this.viewModel = viewModel;
        this.InitializeComponent();
        this.DataContext = viewModel;
    }

    /// <summary>Whether the wizard completed and persisted a configuration.</summary>
    public bool Completed { get; private set; }

    /// <summary>The configuration produced when the wizard completed, if any.</summary>
    public WorkspacesConfiguration? Result { get; private set; }

    private async void OnCompleteClicked(object? sender, RoutedEventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        this.Result = await this.viewModel.SaveAsync();
        this.Completed = true;
        this.Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
