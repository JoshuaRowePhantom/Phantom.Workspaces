using System;
using System.Threading.Tasks;
using Phantom.Workspaces.Agent.Gui;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentSessionWorkspaceTabViewModel : WorkspaceTabViewModel, IAsyncDisposable
{
    public required Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel Agent { get; init; }

    public required ObservableLoggerFactory LoggerFactory { get; init; }

    public async ValueTask DisposeAsync()
    {
        await this.Agent.DisposeAsync();
        this.LoggerFactory.Dispose();
    }
}
