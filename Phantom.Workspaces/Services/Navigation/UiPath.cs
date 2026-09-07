using System;

namespace Phantom.Workspaces.Services.Navigation;

public readonly record struct UiPath
{
    public const string AnyWorkspaceId = "*";

    public UiPath(string workspaceId, string? tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        this.WorkspaceId = workspaceId;
        this.TabId = string.IsNullOrWhiteSpace(tabId) ? null : tabId;
    }

    public string WorkspaceId { get; }

    public string? TabId { get; }

    public bool HasTab => this.TabId is not null;

    public bool HasWorkspace => !string.Equals(this.WorkspaceId, AnyWorkspaceId, StringComparison.Ordinal);

    public static UiPath FromSerializedTabId(string workspaceId, string tabId) => new(workspaceId, tabId);

    public static UiPath ForTab(string? workspaceId, string tabId) =>
        new(string.IsNullOrWhiteSpace(workspaceId) ? AnyWorkspaceId : workspaceId, tabId);

    public override string ToString() => this.TabId ?? this.WorkspaceId;
}
