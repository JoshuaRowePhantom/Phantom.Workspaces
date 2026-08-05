using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// The locked, tab-strip-less <see cref="DocumentDock"/> that hosts the agent-chat detail region
/// (issue #1035). Adding a generated document (e.g. when a sub-agent is added) must never steal the
/// active document away from the node the user currently has selected, so the override adds without
/// activating whenever a document is already active.
/// </summary>
/// <remarks>
/// Uses the thread-agnostic <c>Dock.Model.Mvvm</c> model rather than the <c>Dock.Model.Avalonia</c>
/// <c>ItemsSource</c> variant: sub-agents are added on a background scheduler, and the Avalonia
/// model's dispatcher-affine <c>ItemsSource</c> generation throws a cross-thread access exception
/// when the shared detail-content collection mutates off the UI thread. The cached documents are
/// instead generated imperatively by <see cref="AgentDetailDockFactory"/> from the same flat
/// collection, preserving the cache-N/show-one behaviour without the thread affinity.
/// </remarks>
public sealed class AgentDetailDocumentDock : DocumentDock
{
    public override void AddDocument(IDockable document)
    {
        if (ActiveDockable is not null)
        {
            Factory?.AddDockable(this, document);
        }
        else
        {
            base.AddDocument(document);
        }
    }
}
