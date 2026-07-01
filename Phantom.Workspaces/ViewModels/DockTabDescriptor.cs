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
public abstract record DockTabDescriptor;

/// <summary>Descriptor for a generic entity view tab.</summary>
public sealed record EntityDockTabDescriptor(string EntityId, string ShortcutName) : DockTabDescriptor;

/// <summary>Descriptor for an agent-session tab.</summary>
public sealed record AgentSessionDockTabDescriptor(string EntityId) : DockTabDescriptor;

/// <summary>Descriptor for a browser / web-view tab.</summary>
public sealed record BrowserDockTabDescriptor(string Url) : DockTabDescriptor;
