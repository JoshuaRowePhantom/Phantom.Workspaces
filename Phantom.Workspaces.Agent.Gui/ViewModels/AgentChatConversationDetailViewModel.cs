namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatConversationDetailViewModel : ViewModelBase, IDisposable
{
    public AgentChatConversationDetailViewModel(AgentViewModel agent)
    {
        this.Agent = agent;
        this.StatusLine = new AgentChatStatusLineViewModel(agent);
    }

    public AgentViewModel Agent { get; }

    public InputQueueViewModel InputQueue => this.Agent.InputQueue;

    public AgentChatStatusLineViewModel StatusLine { get; }

    public void Dispose()
    {
        this.StatusLine.Dispose();
    }
}
