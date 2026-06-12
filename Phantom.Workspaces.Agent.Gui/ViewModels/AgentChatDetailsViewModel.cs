namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatDetailsViewModel : ViewModelBase
{
    private string agentSessionId;

    public AgentChatDetailsViewModel(AgentViewModel agent)
    {
        this.Agent = agent;
        this.DisplayName = agent.DisplayName;
        this.agentSessionId = agent.AgentSessionId;
        this.Agent.PropertyChanged += this.OnAgentPropertyChanged;
    }

    public AgentViewModel Agent { get; }

    public string DisplayName { get; }

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
