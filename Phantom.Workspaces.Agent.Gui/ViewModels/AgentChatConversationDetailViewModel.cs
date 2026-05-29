namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatConversationDetailViewModel : ViewModelBase
{
    public AgentChatConversationDetailViewModel(AgentViewModel agent)
    {
        this.Agent = agent;
    }

    public AgentViewModel Agent { get; }

    public InputQueueViewModel InputQueue => this.Agent.InputQueue;
}
