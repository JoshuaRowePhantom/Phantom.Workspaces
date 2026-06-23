using System;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Conventions for identifying Phantom.Workspaces dev tunnels by label. The logical tunnel name is
/// carried as a tunnel label (custom tunnel <c>Name</c>s require a service feature disabled for most
/// accounts), and every Workspaces-owned tunnel additionally carries a stable marker label so a client
/// can auto-discover the single Workspaces tunnel without knowing its name.
/// </summary>
public static class DevTunnelNaming
{
    /// <summary>
    /// The sentinel tunnel name selecting automatic discovery: the client locates the single
    /// Workspaces-owned tunnel (by <see cref="WorkspacesMarkerLabel"/>) instead of matching a name.
    /// </summary>
    public const string AutoSelector = "auto";

    /// <summary>The stable marker label applied to every Workspaces-owned tunnel.</summary>
    public const string WorkspacesMarkerLabel = "phantom-workspaces";

    /// <summary>
    /// Whether <paramref name="tunnelName"/> selects automatic discovery (null/blank, or the
    /// <see cref="AutoSelector"/> sentinel, case-insensitively).
    /// </summary>
    public static bool IsAuto(string? tunnelName)
        => string.IsNullOrWhiteSpace(tunnelName)
            || string.Equals(tunnelName, AutoSelector, StringComparison.OrdinalIgnoreCase);
}
