namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatPlaceholderDetailViewModel : ViewModelBase
{
    public AgentChatPlaceholderDetailViewModel(string title, string description)
    {
        this.Title = title;
        this.Description = description;
    }

    public string Title { get; }

    public string Description { get; }
}
