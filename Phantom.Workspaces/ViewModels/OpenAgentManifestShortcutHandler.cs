using System.Text.Json;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class OpenAgentManifestShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;

    public OpenAgentManifestShortcutHandler(
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
        return shortcut == Shortcut.Open
            && entityViewModel.IsEntityType("agent-manifest");
    }

    public override Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is not JsonElement)
        {
            return Task.FromResult(false);
        }

        var launchpadTab = new AgentManifestLaunchpadViewModel(
            entityViewModel,
            this.agentSessionShortcutContext,
            this.openAgentSessionShortcutHandler,
            mainWindowViewModel)
        {
            Id = $"launchpad-{entityViewModel.EntityId}",
            Title = entityViewModel.DisplayName,
            DockRegion = "full",
            Entity = entityViewModel,
            TabHeader = new IconTabHeaderViewModel { Icon = "🚀", Title = entityViewModel.DisplayName },
        };

        _ = mainWindowViewModel.OpenTabAsync(launchpadTab);
        return Task.FromResult(true);
    }
}

