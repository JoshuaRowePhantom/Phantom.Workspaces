namespace Phantom.Workspaces.Install;

/// <summary>
/// The launch mode selected by the command line. Every mode is a GUI mode; install/update
/// modes show a lightweight progress window instead of the main window.
/// </summary>
public enum LaunchMode
{
    /// <summary>Normal GUI launch (no arguments).</summary>
    Gui,

    /// <summary>Bootstrap into the managed layout, then launch (<c>--install</c>).</summary>
    Install,

    /// <summary>Normal launch honoring startup preferences (<c>--startup</c>).</summary>
    Startup,

    /// <summary>Start hidden/minimized to the tray (<c>--minimized</c>).</summary>
    Minimized,

    /// <summary>Repoint <c>current</c> after the previous process exits (<c>--apply-update</c>).</summary>
    ApplyUpdate,

    /// <summary>Remove shortcuts, startup task, and the managed tree (<c>--uninstall</c>).</summary>
    Uninstall,

    /// <summary>Check for and apply an update headlessly from the CLI (<c>update</c>).</summary>
    Update,

    /// <summary>Show usage (<c>--help</c>/<c>-h</c>).</summary>
    Help,
}
