using System.Collections.Generic;
using System.ComponentModel;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Live profile-appearance controls (theme and debugging) owned by the running main window. These
/// settings apply immediately and persist to the current user profile, independent of the file-based
/// <see cref="Configuration.WorkspacesConfiguration"/>. The unified settings dialog surfaces them as a
/// section so the profile theme and debugging toggles remain reachable from the running application.
/// </summary>
public interface IProfileAppearanceController : INotifyPropertyChanged
{
    /// <summary>The available profile theme names.</summary>
    IReadOnlyList<string> ThemeNames { get; }

    /// <summary>The selected profile theme name; setting it applies and persists the theme live.</summary>
    string SelectedThemeName { get; set; }

    /// <summary>Whether debugging is currently enabled on the profile.</summary>
    bool IsDebuggingEnabled { get; }

    /// <summary>Whether debugging is currently disabled on the profile.</summary>
    bool IsDebuggingDisabled { get; }

    /// <summary>Command that enables or disables debugging on the profile (parameter is a bool).</summary>
    RelayCommand SetDebuggingCommand { get; }
}
