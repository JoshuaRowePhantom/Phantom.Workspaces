using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatDetailsViewModelTests
{
    [Fact]
    public async Task IsReasoningVisible_Setter_UpdatesAgentState()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
        var details = new AgentChatDetailsViewModel(viewModel);

        Assert.False(viewModel.IsReasoningVisible);
        details.IsReasoningVisible = true;

        Assert.True(viewModel.IsReasoningVisible);
        Assert.True(details.IsReasoningVisible);
    }

    [Fact]
    public async Task IsReasoningVisible_ReflectsAgentToggle()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
        var details = new AgentChatDetailsViewModel(viewModel);

        viewModel.ToggleReasoningVisibility();

        Assert.True(details.IsReasoningVisible);
    }

    [Fact]
    public async Task ModelMetadata_ExposesProviderModelAndConnectionTypeWithoutSecrets()
    {
        var chat = await CreateChatAsync(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo",
                "connection": {
                  "kind": "key",
                  "endpoint": "https://example.invalid",
                  "apiKey": "do-not-show-this"
                }
              },
              "tools": []
            }
            """);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
        var details = new AgentChatDetailsViewModel(viewModel);

        Assert.Equal("echo", details.ModelProvider);
        Assert.Equal("test", details.ModelId);
        Assert.Equal("Echo", details.ModelApiType);
        Assert.Equal("API key", details.ModelConnectionType);
        Assert.DoesNotContain("do-not-show-this", details.ModelConnectionType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentName_ExposesCallerSuppliedName()
    {
        // Fix #1151: the caller-supplied sub-agent name (from AgentChat.Name) surfaces on the
        // chat details pane so the operator can distinguish e.g. fix-crash1142 from the
        // type-level DisplayName.
        var chat = await CreateChatWithNameAsync(nameOverride: "fix-crash1142");
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "General purpose", "", loggerFactory, TaskScheduler.Default);
        var details = new AgentChatDetailsViewModel(viewModel);

        Assert.Equal("fix-crash1142", details.AgentName);
        // The type-level display name must remain intact — issue #1133 must not regress.
        Assert.Equal("General purpose", details.DisplayName);
    }

    [Fact]
    public async Task AgentName_TwoSubAgentsWithDifferentNames_AreDistinguishable()
    {
        // Fix #1151: distinct caller-supplied names must appear on the two AgentChat instances
        // even when their agent-type DisplayName is identical.
        var chatA = await CreateChatWithNameAsync(nameOverride: "fix-crash1142");
        var chatB = await CreateChatWithNameAsync(nameOverride: "fix-reload1");

        using var loggerFactory = new ObservableLoggerFactory();
        await using var vmA = new AgentViewModel(chatA, "General purpose", "", loggerFactory, TaskScheduler.Default);
        await using var vmB = new AgentViewModel(chatB, "General purpose", "", loggerFactory, TaskScheduler.Default);

        var detailsA = new AgentChatDetailsViewModel(vmA);
        var detailsB = new AgentChatDetailsViewModel(vmB);

        Assert.Equal("fix-crash1142", detailsA.AgentName);
        Assert.Equal("fix-reload1", detailsB.AgentName);
        Assert.NotEqual(detailsA.AgentName, detailsB.AgentName);
    }

    private static Task<AgentChat> CreateChatWithNameAsync(string? nameOverride)
        => AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = CreateAgentDefinition(),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "General purpose",
            NameOverride = nameOverride,
        });

    private static AgentDefinition CreateAgentDefinition(string? definitionJson = null)
        => AgentDefinitionLoader.LoadAgentFromJson(
            definitionJson ??
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

    private static Task<AgentChat> CreateChatAsync(
        string? definitionJson = null,
        AgentServices? agentServices = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(definitionJson),
                AgentServices = agentServices,
            });
}
