namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatDetailsViewModel : ViewModelBase
{
    private string agentSessionId;

    public AgentChatDetailsViewModel(AgentViewModel agent)
    {
        this.Agent = agent;
        this.DisplayName = agent.DisplayName;
        this.AgentName = agent.Name;
        this.agentSessionId = agent.AgentSessionId;
        this.Agent.PropertyChanged += this.OnAgentPropertyChanged;
    }

    public AgentViewModel Agent { get; }

    public string DisplayName { get; }

    /// <summary>
    /// Caller-supplied sub-agent name/id (issue #1151), e.g. <c>fix-crash1142</c>. Independent of
    /// <see cref="DisplayName"/>: the display name is the agent-type label; the agent name is the
    /// invoker-chosen identity. Empty when no caller name was supplied.
    /// </summary>
    public string AgentName { get; }

    public string AgentSessionId
    {
        get => this.agentSessionId;
        private set => this.SetProperty(ref this.agentSessionId, value);
    }

    public string ModelProvider => this.Agent.ModelProvider;

    public string ModelId => this.Agent.ModelId;

    public string ModelApiType => this.Agent.ModelApiType;

    public string ModelConnectionType => this.Agent.ModelConnectionType;

    public bool IsReasoningVisible
    {
        get => this.Agent.IsReasoningVisible;
        set => this.Agent.SetReasoningVisibility(value);
    }

    public void UpdateSessionId(string sessionId)
        => this.AgentSessionId = sessionId;

    private void OnAgentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AgentViewModel.IsReasoningVisible), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsReasoningVisible));
        }
    }
}
