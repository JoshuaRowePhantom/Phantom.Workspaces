using System;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Settings section that surfaces the running application's live profile theme and debugging controls
/// inside the unified settings dialog. It delegates to the <see cref="IProfileAppearanceController"/>
/// (the main window view model), whose theme/debugging changes apply immediately and persist to the
/// user profile, so nothing from the legacy standalone settings window is lost.
/// </summary>
public sealed class ProfileAppearanceSettingsViewModel : ViewModelBase
{
    /// <summary>Creates the section over the supplied live profile-appearance controller.</summary>
    public ProfileAppearanceSettingsViewModel(IProfileAppearanceController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        this.Controller = controller;
    }

    /// <summary>The live profile-appearance controller this section binds to.</summary>
    public IProfileAppearanceController Controller { get; }
}
