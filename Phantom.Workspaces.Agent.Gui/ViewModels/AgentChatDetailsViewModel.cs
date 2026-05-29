namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatDetailsViewModel : ViewModelBase
{
    private string agentSessionId;

    public AgentChatDetailsViewModel(AgentViewModel agent)
    {
        this.Agent = agent;
        this.DisplayName = agent.DisplayName;
        this.agentSessionId = agent.AgentSessionId;
    }

    public AgentViewModel Agent { get; }

    public string DisplayName { get; }

    public string AgentSessionId
    {
        get => this.agentSessionId;
        private set => this.SetProperty(ref this.agentSessionId, value);
    }

    public void UpdateSessionId(string sessionId)
        => this.AgentSessionId = sessionId;
}
