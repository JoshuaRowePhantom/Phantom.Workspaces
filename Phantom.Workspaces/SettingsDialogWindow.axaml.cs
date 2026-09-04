using System;
using System.Threading.Tasks;
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

    /// <summary>The last save-failure message shown in the dialog, or <c>null</c> when none.</summary>
    internal string? SaveErrorMessage { get; private set; }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
        => await this.TrySaveAsync();

    /// <summary>
    /// Saves the settings, catching any failure so a leaked Save-path exception can never reach the
    /// dispatcher (issue #1349). On failure the message is surfaced in an in-dialog banner instead
    /// of crashing the application; the dialog stays open.
    /// </summary>
    internal async Task TrySaveAsync()
    {
        if (this.viewModel is null)
        {
            return;
        }

        try
        {
            this.SetSaveError(null);
            this.Result = await this.viewModel.SaveAsync();
            this.Saved = true;
            this.Close();
        }
        catch (Exception exception)
        {
            this.SetSaveError($"Saving settings failed: {exception.Message}");
        }
    }

    private void SetSaveError(string? message)
    {
        this.SaveErrorMessage = message;
        var banner = this.FindControl<TextBlock>("SaveErrorBanner");
        if (banner is not null)
        {
            banner.Text = message;
            banner.IsVisible = !string.IsNullOrEmpty(message);
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
