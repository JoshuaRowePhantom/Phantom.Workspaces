namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// Canonical descriptor of a navigation target shared by every tab-navigation call site
/// (the running-agent brain button, the notifications dropdown, and the Ctrl nav-stack popup).
/// Either <see cref="TabId"/> identifies an existing (or open-but-unselected) tab, or — for the
/// brain-button fallback — <see cref="AgentSessionKey"/> identifies an agent session whose entity
/// should be opened when no tab is currently open for it. See issue #1254.
/// </summary>
/// <remarks>
/// Constructed via object initializers (designated initializers) rather than a positional
/// constructor, so call sites read as <c>new NavigationTarget { TabId = ..., WorkspacePaneId = ... }</c>.
/// </remarks>
public sealed record NavigationTarget
{
    /// <summary>The id of the tab to activate, or <see langword="null"/> to use the entity fallback.</summary>
    public string? TabId { get; init; }

    /// <summary>An optional hint for the workspace pane that owns the tab (opened if not yet loaded).</summary>
    public string? WorkspacePaneId { get; init; }

    /// <summary>The entity to open when <see cref="TabId"/> is <see langword="null"/> (brain fallback).</summary>
    public string? EntityId { get; init; }

    /// <summary>The agent session key used to resolve the fallback entity/pane (brain fallback).</summary>
    public string? AgentSessionKey { get; init; }
}
