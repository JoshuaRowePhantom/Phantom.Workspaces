using System.Reflection;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatStatusLineViewModelTests
{
    [Fact]
    public async Task EmptyAgent_ShowsNoneDisplaysAndNoTokens()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(agentDefinition: null), "empty-agent", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agentViewModel);

        Assert.Equal("(none)", statusLine.ModelDisplay);
        Assert.Equal("(none)", statusLine.ProviderDisplay);
        Assert.Null(statusLine.TokensDisplay);
        Assert.False(statusLine.HasTokens);
        Assert.True(statusLine.HasModel);
        Assert.True(statusLine.HasProvider);
        Assert.True(statusLine.HasVisibleContent);
    }

    [Fact]
    public async Task ModelAndProvider_DisplayResolvedAgentModel()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(CreateAgentDefinition()), "test-agent", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agentViewModel);

        Assert.Equal("gpt-4o", statusLine.ModelDisplay);
        Assert.Equal("github-models", statusLine.ProviderDisplay);
        Assert.True(statusLine.HasModel);
        Assert.True(statusLine.HasProvider);
    }

    [Fact]
    public async Task IsThinking_FollowsRunningItems()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(CreateAgentDefinition()), "test-agent", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agentViewModel);

        var runningItem = agentViewModel.AgentChat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("thinking")],
        });

        Assert.True(statusLine.IsThinking);

        agentViewModel.AgentChat.CompleteRunningItem(runningItem, writeToHistory: false);

        Assert.False(statusLine.IsThinking);
    }

    [PhantomAvaloniaFact]
    public async Task TokensDisplay_FormatsOnlyWhenBothTotalsArePresent()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(CreateAgentDefinition()), "test-agent", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agentViewModel);

        SetTokenCountsAndRaiseUsageChanged(agentViewModel.AgentChat, inputTokenCount: 1234, outputTokenCount: null);

        Assert.Null(statusLine.TokensDisplay);
        Assert.False(statusLine.HasTokens);

        SetTokenCountsAndRaiseUsageChanged(agentViewModel.AgentChat, inputTokenCount: 1234, outputTokenCount: 56);

        Assert.Equal("1,234 in / 56 out", statusLine.TokensDisplay);
        Assert.True(statusLine.HasTokens);
    }

    [Fact]
    public async Task IsReasoningVisible_WhenAgentPropertyChanges_PropagatesChange()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(CreateAgentDefinition()), "test-agent", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agentViewModel);

        var changedProperties = new List<string?>();
        statusLine.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        agentViewModel.SetReasoningVisibility(true);

        Assert.Contains(nameof(AgentChatStatusLineViewModel.IsReasoningVisible), changedProperties);
        Assert.True(statusLine.IsReasoningVisible);

        changedProperties.Clear();
        agentViewModel.SetReasoningVisibility(false);

        Assert.Contains(nameof(AgentChatStatusLineViewModel.IsReasoningVisible), changedProperties);
        Assert.False(statusLine.IsReasoningVisible);
    }

    [Fact]
    public async Task ReasoningIndicatorText_WhenIsReasoningVisibleIsTrue_ReturnsShowingText()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(CreateAgentDefinition()), "test-agent", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agentViewModel);

        agentViewModel.SetReasoningVisibility(true);

        Assert.Equal("🧠 Showing Reasoning", statusLine.ReasoningIndicatorText);
    }

    [Fact]
    public async Task ReasoningIndicatorText_WhenIsReasoningVisibleIsFalse_ReturnsNotShowingText()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(CreateAgentDefinition()), "test-agent", "", loggerFactory);
        using var statusLine = new AgentChatStatusLineViewModel(agentViewModel);

        agentViewModel.SetReasoningVisibility(false);

        Assert.Equal("🚫🧠 Not Showing Reasoning", statusLine.ReasoningIndicatorText);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromAgentChanges()
    {
        using var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(CreateChat(CreateAgentDefinition()), "test-agent", "", loggerFactory);
        var statusLine = new AgentChatStatusLineViewModel(agentViewModel);
        statusLine.Dispose();

        agentViewModel.AgentChat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("thinking")],
        });

        Assert.False(statusLine.IsThinking);
    }

    private static AgentChat CreateChat(AgentDefinition? agentDefinition)
    {
        var requestType = typeof(AgentChat).Assembly.GetType("Phantom.Workspaces.Llm.InternalCreateAgentChatRequest")
            ?? throw new InvalidOperationException("InternalCreateAgentChatRequest type was not found.");
        var request = Activator.CreateInstance(requestType)
            ?? throw new InvalidOperationException("InternalCreateAgentChatRequest could not be created.");
        var agentDefinitionProperty = requestType.GetProperty("AgentDefinition")
            ?? throw new InvalidOperationException("AgentDefinition property was not found.");
        var configuredStoreProperty = requestType.GetProperty("ConfiguredStore")
            ?? throw new InvalidOperationException("ConfiguredStore property was not found.");
        agentDefinitionProperty.SetValue(request, agentDefinition);
        configuredStoreProperty.SetValue(request, new InMemoryAgentPersistenceStore());

        var constructor = typeof(AgentChat).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [requestType],
            modifiers: null)
            ?? throw new InvalidOperationException("AgentChat constructor was not found.");

        var agentChat = (AgentChat)constructor.Invoke([request]);
        var agentDefinitionField = typeof(AgentChat).GetField(
            "agentDefinition",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("agentDefinition field was not found.");
        agentDefinitionField.SetValue(agentChat, agentDefinition);
        return agentChat;
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "gpt-4o",
                "provider": "github-models",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

    private static void SetTokenCountsAndRaiseUsageChanged(
        AgentChat agentChat,
        long? inputTokenCount,
        long? outputTokenCount)
    {
        SetBackingField(agentChat, nameof(AgentChat.TotalInputTokenCount), inputTokenCount);
        SetBackingField(agentChat, nameof(AgentChat.TotalOutputTokenCount), outputTokenCount);

        var usageChangedField = typeof(AgentChat).GetField(
            "UsageChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UsageChanged event field was not found.");
        var usageChanged = (EventHandler?)usageChangedField.GetValue(agentChat);
        usageChanged?.Invoke(agentChat, EventArgs.Empty);
    }

    private static void SetBackingField<TValue>(AgentChat agentChat, string propertyName, TValue value)
    {
        var field = typeof(AgentChat).GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{propertyName} backing field was not found.");
        field.SetValue(agentChat, value);
    }
}
