using System;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Appearance/visual settings section. Currently exposes the selected theme; kept as its own
/// section view model so it slots into the settings dialog's master-detail layout alongside the
/// repository and remote-access sections.
/// </summary>
public sealed class AppearanceSettingsViewModel : ViewModelBase
{
    private string theme;

    public AppearanceSettingsViewModel(string theme)
    {
        this.theme = theme ?? string.Empty;
    }

    /// <summary>The selected visual theme.</summary>
    public string Theme
    {
        get => this.theme;
        set => this.SetProperty(ref this.theme, value ?? string.Empty);
    }
}
