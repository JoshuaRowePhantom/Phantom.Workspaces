using System;
using System.IO;
using System.Linq;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelSubAgentTreeFilterTests
{
    [Fact]
    public async Task SubAgentsTree_HideCompleted_DefaultsToTrue()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        Assert.True(subAgentsNav.HideCompletedAgents);
    }

    [Fact]
    public async Task SubAgentsRoot_ShowsHideCompletedToggle_ButOtherNavItemsDoNot()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");
        var chatDetailsNav = root.Children.Single(c => c.Id == "chat-details");
        var toolsNav = root.Children.Single(c => c.Id == "chat-tools");

        Assert.True(subAgentsNav.ShowHideCompletedToggle);
        Assert.False(chatDetailsNav.ShowHideCompletedToggle);
        Assert.False(toolsNav.ShowHideCompletedToggle);
        Assert.False(root.ShowHideCompletedToggle);
    }

    [Fact]
    public async Task SubAgentsTree_WhenHideCompletedTrue_ExcludesSucceededAndFailed()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "running", "Running Agent");
        await AddSubAgentAsync(chat, "done", "Done Agent");
        await AddSubAgentAsync(chat, "broke", "Broken Agent");

        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "done")).SetCompletionState(AgentChatCompletionState.Succeeded);
        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "broke")).SetCompletionState(AgentChatCompletionState.Failed);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        // In production each sub-agent's CompletionStateChanged event re-applies the filter; the
        // echo test agents do not raise that event, so re-apply the (default-on) filter explicitly.
        subAgentsNav.HideCompletedAgents = false;
        subAgentsNav.HideCompletedAgents = true;

        var visibleIds = subAgentsNav.Children.Select(c => c.Id).ToList();
        Assert.Equal(new[] { "sub-agent-running" }, visibleIds);

        // The count label still reflects the total number of sub-agents.
        Assert.Equal("Sub-agents (3)", subAgentsNav.Name);
    }

    [Fact]
    public async Task SubAgentsTree_WhenHideCompletedFalse_ShowsAllAgents()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "running", "Running Agent");
        await AddSubAgentAsync(chat, "done", "Done Agent");

        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "done")).SetCompletionState(AgentChatCompletionState.Succeeded);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        subAgentsNav.HideCompletedAgents = false;

        var visibleIds = subAgentsNav.Children.Select(c => c.Id).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "sub-agent-done", "sub-agent-running" }, visibleIds);
    }

    [Fact]
    public async Task SubAgentsTree_WhenAgentCompletes_AndHideCompleted_IsRemoved()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "worker", "Worker Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.Single(c => c.Id == "chat-sub-agents");

        // While running (default hide-completed = true) the agent is visible.
        Assert.Contains(subAgentsNav.Children, c => c.Id == "sub-agent-worker");

        // It transitions to completed...
        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "worker")).SetCompletionState(AgentChatCompletionState.Succeeded);

        // ...and the filter re-runs (production: via CompletionStateChanged), removing it.
        subAgentsNav.HideCompletedAgents = false;
        subAgentsNav.HideCompletedAgents = true;

        Assert.DoesNotContain(subAgentsNav.Children, c => c.Id == "sub-agent-worker");
    }

    [Fact]
    public void AgentNavigationHeader_ShowsHideCompletedCheckbox_OnSubAgentsRoot()
    {
        var axamlContent = ReadAxaml("AgentChatToolTemplates.axaml");

        var start = axamlContent.IndexOf("x:Key=\"AgentNavigationHeaderTemplate\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = axamlContent.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        var navHeader = axamlContent[start..end];

        Assert.Contains("Content=\"Hide completed\"", navHeader, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowHideCompletedToggle}\"", navHeader, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding HideCompletedAgents, Mode=TwoWay}\"", navHeader, StringComparison.Ordinal);
    }

    private static string ReadAxaml(string fileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Agent.Gui",
            "Controls",
            fileName);

        return File.ReadAllText(filePath);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
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

    private static Task<AgentChat> CreateChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
            });

    private static async Task AddSubAgentAsync(AgentChat chat, string agentId, string displayName)
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{displayName}}",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        await chat.GetOrCreateAsync(agentId, definition, $"tool-call-{agentId}", TestContext.Current.CancellationToken);
    }
}
