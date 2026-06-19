using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class StartAgentSessionOnProfileShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;

    public StartAgentSessionOnProfileShortcutHandler(
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler)
    {
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
    }

    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.StartAgentSession
            && entityViewModel.IsEntityType("user-computer-profile");
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        // Create the start agent session tab that will show agent definition selection UI
        var startAgentSessionTab = new StartAgentSessionOnProfileViewModel(
            mainWindowViewModel,
            this.agentSessionShortcutContext,
            this.openAgentSessionShortcutHandler,
            mainWindowViewModel,
            entityViewModel)
        {
            Id = $"start-agent-session-{entityViewModel.EntityId}",
            Title = $"Start Agent Session on {entityViewModel.DisplayName}",
        };

        await mainWindowViewModel.OpenTabAsync(startAgentSessionTab);
        return true;
    }
}
