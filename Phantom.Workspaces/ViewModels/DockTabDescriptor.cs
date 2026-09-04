using System.Text.Json.Serialization;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Polymorphic serializable descriptor embedded in each <see cref="WorkspaceDocument"/> node
/// of a dock-layout JSON blob. Carries the identity information needed to recreate the tab's
/// view model on restore. Concrete subtypes encode only the properties relevant to their kind.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(EntityDockTabDescriptor), "entity")]
[JsonDerivedType(typeof(AgentSessionDockTabDescriptor), "agent-session")]
[JsonDerivedType(typeof(BrowserDockTabDescriptor), "browser")]
public abstract record DockTabDescriptor
{
    /// <summary>
    /// The tab title captured at save time. Preferred over live entity re-derivation on restore
    /// so that user-visible titles round-trip through the persisted dock layout even when the
    /// referenced entity's display-name is empty or has changed.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// True when <see cref="Title"/> is a user/host override rather than content-derived.
    /// Content-derived title updates must not overwrite explicit titles after restore.
    /// </summary>
    public bool IsTitleExplicit { get; init; }
}

/// <summary>Descriptor for a generic entity view tab.</summary>
public sealed record EntityDockTabDescriptor(string EntityId, string ShortcutName) : DockTabDescriptor;

/// <summary>Descriptor for an agent-session tab.</summary>
public sealed record AgentSessionDockTabDescriptor(string EntityId) : DockTabDescriptor;

/// <summary>Descriptor for a browser / web-view tab.</summary>
public sealed record BrowserDockTabDescriptor(string Url) : DockTabDescriptor;
