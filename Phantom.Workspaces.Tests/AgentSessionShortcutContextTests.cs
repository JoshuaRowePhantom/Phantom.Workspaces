using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

// Issue #1236: get_current_session returned empty {} on Copilot / running-agent sessions because
// the GUI shortcut path constructed a session-id-only context. The fix routes the GUI path through
// the shared CurrentSessionContextFactory. This test asserts the GUI shortcut-context path delegates
// to that shared factory so the resolved user / computer / profile identity matches exactly, rather
// than resolving identity via a separate code path.
public sealed class AgentSessionShortcutContextTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentSessionShortcutContext_BuildCurrentSessionContext_DelegatesToSharedFactory()
    {
        var userName = Environment.UserName;
        var computerName = Environment.MachineName;

        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();

        // InitializeAsync bootstraps the current user / computer / user-computer-profile entities
        // (no profile override), so the shared factory has real host-identity entities to resolve.
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var dataAccessLayer = entityBroker.EntityRepository.DataAccessLayer;

        var shortcutContext = new AgentSessionShortcutContext();

        var services = await shortcutContext.CreateAgentServicesAsync(viewModel);

        // The GUI path stashes the resolved host context on the returned AgentServices (issue #1236).
        var actual = Assert.IsType<CurrentSessionContext>(services.CurrentSessionContext);

        Assert.Equal(string.Empty, actual.AgentSessionId);
        Assert.NotNull(actual.User);
        Assert.NotNull(actual.Computer);
        Assert.NotNull(actual.UserComputerProfile);

        // Prove delegation: the GUI path must produce exactly what the shared factory produces for
        // the same host identity (same session id, same resolved user / computer / profile entities).
        var expected = await CurrentSessionContextFactory.CreateForHostAsync(
            agentSessionId: string.Empty,
            dataAccessLayer: dataAccessLayer,
            userName: userName,
            computerName: computerName,
            effectiveComputerName: computerName,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(expected.User);
        Assert.NotNull(expected.Computer);
        Assert.NotNull(expected.UserComputerProfile);
        Assert.Equal(expected.User!.EntityId, actual.User!.EntityId);
        Assert.Equal(expected.Computer!.EntityId, actual.Computer!.EntityId);
        Assert.Equal(expected.UserComputerProfile!.EntityId, actual.UserComputerProfile!.EntityId);
    }
}
