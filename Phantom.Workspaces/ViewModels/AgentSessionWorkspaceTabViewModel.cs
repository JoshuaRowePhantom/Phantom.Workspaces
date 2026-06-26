using System;
using System.Threading.Tasks;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.ViewModels;

public enum AgentTabState
{
    Loading,
    Ready,
    Failed,
}

public sealed class AgentSessionWorkspaceTabViewModel : WorkspaceTabViewModel, IAsyncDisposable
{
    private AgentTabState state = AgentTabState.Loading;
    private string? loadError;
    private AgentViewModel? agent;
    private ObservableLoggerFactory? loggerFactory;

    public AgentTabState State
    {
        get => this.state;
        private set => this.SetProperty(ref this.state, value);
    }

    public string? LoadError
    {
        get => this.loadError;
        private set => this.SetProperty(ref this.loadError, value);
    }

    public AgentViewModel? Agent
    {
        get => this.agent;
        private set => this.SetProperty(ref this.agent, value);
    }

    public ObservableLoggerFactory? LoggerFactory => this.loggerFactory;

    public void SetReady(AgentViewModel agentViewModel, ObservableLoggerFactory factory)
    {
        this.loggerFactory = factory;
        this.Agent = agentViewModel;
        this.State = AgentTabState.Ready;
    }

    public void SetFailed(string error)
    {
        this.LoadError = error;
        this.State = AgentTabState.Failed;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.agent is not null)
        {
            await this.agent.DisposeAsync();
        }

        this.loggerFactory?.Dispose();
    }
}
