using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.ViewModels;
using Xunit;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;

namespace Phantom.Workspaces.Tests;

public sealed class OpenAgentSessionShortcutHandlerTests
{
    [AvaloniaFact(Timeout = 30_000)]
    public async Task ComposeSessionAgentViewModel_AlwaysConfiguresSlashCommands()
    {
        // #1429: ComposeSessionAgentViewModel is the single seam every GUI session launch path
        // routes through. Calling it directly must always leave slash commands wired, so no future
        // launch path can bypass slash-command configuration by depending on caller-side wiring.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var definitionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("bbbb0005-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "bbbb0005-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "slash-seam"]],
              "display-name": { "default": "Echo slash-seam" },
              "definition": {
                "kind": "prompt",
                "name": "slash-seam",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var context = new AgentSessionShortcutContext();
        var sessionEntity = await context.CreateAgentSessionEntityAsync(
            viewModel, definitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable());

        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind": "prompt",
                  "name": "slash-seam",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                  "tools": []
                }
                """),
            ForegroundScheduler = TaskScheduler.Default,
        });

        var loggerFactory = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = sessionEntity!.EntityId.ToString(),
            Title = sessionEntity.DisplayName,
            Entity = sessionEntity,
        };

        AgentViewModel agent = handler.ComposeSessionAgentViewModel(
            viewModel, loggerFactory, chat, sessionEntity, tab, TaskScheduler.Default);

        try
        {
            await MainWindowIntegrationTests.AssertSlashCommandsEnabledAsync(agent);
        }
        finally
        {
            await agent.DisposeAsync();
            await chat.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
