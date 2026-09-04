namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// Per-call-site concerns for a single <see cref="ITabNavigator.NavigateAsync"/> invocation. Each
/// affordance carries its own options while the navigator itself remains the single source of truth
/// for <em>how</em> a tab is navigated to. See issue #1254.
/// </summary>
/// <remarks>
/// Constructed via object initializers (designated initializers) rather than a positional
/// constructor. The defaults match the "universal" navigation behaviour: push a history entry and
/// mark the target tab's notification read. The Ctrl-popup replay path sets
/// <see cref="PushHistory"/> to <see langword="false"/>; the notifications path sets
/// <see cref="FocusWindow"/> to <see langword="true"/>; the brain-button fallback sets
/// <see cref="OpenEntityIfNoTab"/> to <see langword="true"/>.
/// </remarks>
public sealed record NavigationOptions
{
    /// <summary>Whether to push a <c>NavigationEntry</c> onto the history stack (notifications: true; Ctrl replay: false).</summary>
    public bool PushHistory { get; init; } = true;

    /// <summary>Whether to mark the target tab's notification read — universal, fixes the Ctrl-popup gap w.r.t. #1166.</summary>
    public bool MarkNotificationRead { get; init; } = true;

    /// <summary>Whether to focus the main window before navigating (notifications: true).</summary>
    public bool FocusWindow { get; init; }

    /// <summary>Whether to open the target entity when no tab is open for it (brain-button fallback).</summary>
    public bool OpenEntityIfNoTab { get; init; }
}
