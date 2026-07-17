namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// One source item of the flat detail-content collection that feeds the agent-chat detail
/// <see cref="Dock.Avalonia.Controls.DockControl"/> (issue #1035). Exactly one item exists per
/// navigation node / detail slot — including every sub-agent child node — so each nav node's
/// <c>DetailContent</c> always has a first-class cached document to render.
/// </summary>
public sealed class AgentDetailDocumentItem : ViewModelBase
{
    private bool isActive;

    public AgentDetailDocumentItem(string key, string title, object content)
    {
        this.Key = key;
        this.Title = title;
        this.Content = content;
    }

    /// <summary>Stable, tree-unique id. Drives <c>Document.Id</c> and node→document lookup.</summary>
    public string Key { get; }

    /// <summary>Display title (only rendered if the tab strip were shown; set for parity).</summary>
    public string Title { get; }

    /// <summary>The actual detail view-model the cached document's content binds to.</summary>
    public object Content { get; }

    /// <summary>Active-state mirror of the dock's active document for this item.</summary>
    public bool IsActive
    {
        get => this.isActive;
        set => this.SetProperty(ref this.isActive, value);
    }
}
