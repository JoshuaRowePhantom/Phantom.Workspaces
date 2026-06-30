namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatDiagnosticsDetailViewModel : ViewModelBase, IDisposable
{
    public AgentChatDiagnosticsDetailViewModel(AgentViewModel agent)
    {
        this.Agent = agent;
        this.StatusLine = new DiagnosticsStatusLineViewModel();
    }

    public AgentViewModel Agent { get; }

    public DiagnosticsStatusLineViewModel StatusLine { get; }

    public void Dispose()
    {
    }
}
