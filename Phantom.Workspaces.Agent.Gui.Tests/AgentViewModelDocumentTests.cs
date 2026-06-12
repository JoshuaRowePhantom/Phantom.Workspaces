using System.Reflection;
using AgentSchema;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelDocumentTests
{
    [AvaloniaFact]
    public async Task LiveCollections_RenderHistoryAndRunningItemsInOrder()
    {
        await using var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        chat.EnqueueUserMessage("hello");
        await WaitForConditionAsync(chat.History, () => viewModel.History.Count >= 2, "history to populate");

        Assert.Equal(2, viewModel.History.Count);
        Assert.Equal(2, GetHistoryRoot(viewModel).Blocks.OfType<Section>().Count());

        var runningItem = chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("thinking")],
        });

        await WaitForConditionAsync(chat.RunningItems, () => viewModel.RunningItems.Count == 1, "running item to appear");

        Assert.Single(viewModel.RunningItems);
        Assert.Equal(1, GetRunningRoot(viewModel).Blocks.OfType<Section>().Count());

        chat.UpdateRunningItem(runningItem, [new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("done")],
        }]);

        Assert.Contains("done", GetText(viewModel.RunningItems[0].Items[0].Contents), StringComparison.Ordinal);

        chat.CompleteRunningItem(runningItem, writeToHistory: false);
        await WaitForConditionAsync(chat.RunningItems, () => viewModel.RunningItems.Count == 0, "running item to clear");

        Assert.Empty(viewModel.RunningItems);
        Assert.Empty(GetRunningRoot(viewModel).Blocks.OfType<Section>());
    }

    [AvaloniaFact]
    public async Task AgentSessionIdChanged_UpdatesTheViewModel()
    {
        await using var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        chat.SetAgentSessionId("new-session-id");

        Assert.Equal("new-session-id", viewModel.AgentSessionId);
    }

    [AvaloniaFact]
    public async Task LiveCollections_UpdateSelectableOutputText()
    {
        await using var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        chat.EnqueueUserMessage("hello text model");
        await WaitForConditionAsync(chat.History, () => viewModel.History.Count >= 2, "history to populate");

        Assert.Contains("hello text model", viewModel.OutputText, StringComparison.Ordinal);
        var runningItem = chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("in progress")],
        });
        await WaitForConditionAsync(chat.RunningItems, () => viewModel.RunningItems.Count == 1, "running item to appear");

        Assert.Contains("assistant (running)", viewModel.OutputText, StringComparison.Ordinal);
        Assert.Contains("in progress", viewModel.OutputText, StringComparison.Ordinal);

        chat.CompleteRunningItem(runningItem, writeToHistory: false);
        await WaitForConditionAsync(chat.RunningItems, () => viewModel.RunningItems.Count == 0, "running item to clear");

        Assert.DoesNotContain("assistant (running)", viewModel.OutputText, StringComparison.Ordinal);
    }

    private static async Task WaitForConditionAsync(
        System.Collections.Specialized.INotifyCollectionChanged collection,
        Func<bool> condition,
        string description)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (condition())
            {
                return;
            }

            await signal.Task;
        }
        finally
        {
            collection.CollectionChanged -= OnCollectionChanged;
        }
    }

    private static async Task<AgentChat> CreateChatAsync()
        => await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
            });

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

    private static Section GetHistoryRoot(AgentViewModel viewModel)
    {
        var roots = viewModel.OutputDocument.Blocks.OfType<Section>().ToArray();
        Assert.True(roots.Length >= 2);
        return roots[0];
    }

    private static Section GetRunningRoot(AgentViewModel viewModel)
    {
        var roots = viewModel.OutputDocument.Blocks.OfType<Section>().ToArray();
        Assert.True(roots.Length >= 2);
        return roots[1];
    }

    private static string GetText(IEnumerable<AIContent> contents)
        => string.Concat(contents.OfType<TextContent>().Select(static content => content.Text));
}
