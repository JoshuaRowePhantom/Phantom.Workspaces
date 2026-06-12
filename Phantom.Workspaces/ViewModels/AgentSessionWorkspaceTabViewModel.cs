using System;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentSessionWorkspaceTabViewModel : WorkspaceTabViewModel, IAsyncDisposable
{
    public required Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel Agent { get; init; }

    public ValueTask DisposeAsync() => this.Agent.DisposeAsync();
}
