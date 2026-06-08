using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatHistoryItemViewModelTests
{
    private static AgentDefinition CreateTestProviderAgentDefinition()
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
                AgentDefinition = CreateTestProviderAgentDefinition(),
            });

    [AvaloniaFact]
    public void Constructor_UserRole_MapsToUserLabel()
    {
        var source = new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Contents = [new TextContent("hello")],
        };

        var viewModel = new ChatHistoryItemViewModel(source);

        Assert.True(viewModel.IsUser);
        Assert.Equal("user", viewModel.RoleLabel);
        Assert.Equal("hello", viewModel.Text);
    }

    [AvaloniaFact]
    public void Constructor_AssistantRole_MapsToAssistantLabel()
    {
        var source = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("response"), new TextReasoningContent("hidden reasoning")],
        };

        var viewModel = new ChatHistoryItemViewModel(source, isInProgress: true);

        Assert.False(viewModel.IsUser);
        Assert.Equal("assistant", viewModel.RoleLabel);
        Assert.Equal("response", viewModel.Text);
        Assert.Empty(viewModel.ReasoningDisplayText);
        Assert.False(viewModel.HasReasoningLine);
    }

    [AvaloniaFact]
    public void Constructor_AssistantInProgressWithoutText_ShowsThinking()
    {
        var source = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
        };

        var viewModel = new ChatHistoryItemViewModel(source, isInProgress: true);

        Assert.Equal("Thinking ...", viewModel.ReasoningDisplayText);
        Assert.True(viewModel.HasReasoningLine);
    }

    [AvaloniaFact]
    public void Constructor_AssistantInProgressWithText_DoesNotShowThinking()
    {
        var source = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("hello")],
        };

        var viewModel = new ChatHistoryItemViewModel(source, isInProgress: true);

        Assert.Empty(viewModel.ReasoningDisplayText);
        Assert.False(viewModel.HasReasoningLine);
    }

    [AvaloniaFact]
    public void UpdateFrom_UpdatesTextAndProgressState()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streaming")],
        }, isInProgress: true);

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("complete"), new TextReasoningContent("because")],
        });

        Assert.Equal("complete", viewModel.Text);
        Assert.Empty(viewModel.ReasoningDisplayText);
    }

    [AvaloniaFact]
    public void UpdateFrom_CompletingResponse_RefreshesReasoningVisibility()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streaming")],
        }, isInProgress: true);

        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("complete"), new TextReasoningContent("because")],
        });

        Assert.Contains(nameof(ChatHistoryItemViewModel.HasReasoningLine), changed);
        Assert.Contains(nameof(ChatHistoryItemViewModel.ReasoningDisplayText), changed);
        Assert.False(viewModel.HasReasoningLine);
        Assert.Empty(viewModel.ReasoningDisplayText);
    }

    [AvaloniaFact]
    public void SetReasoningVisible_ShowsReasoningText()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("answer"), new TextReasoningContent("step 1")],
        }, isInProgress: true);

        viewModel.SetReasoningVisible(true);

        Assert.Equal("step 1", viewModel.ReasoningDisplayText);
    }

    [AvaloniaFact]
    public void SetReasoningVisible_WhenNotInProgress_ShowsReasoningText()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("answer"), new TextReasoningContent("step 1")],
        });

        viewModel.SetReasoningVisible(true);

        Assert.Equal("step 1", viewModel.ReasoningDisplayText);
        Assert.True(viewModel.HasReasoningLine);
    }

    [AvaloniaFact]
    public void Constructor_WithImageContent_ExposesAttachmentPreview()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Contents = [new DataContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3ZfV0AAAAASUVORK5CYII="), "image/png")],
        });

        var attachment = Assert.Single(viewModel.Attachments);

        Assert.True(viewModel.HasAttachments);
        Assert.StartsWith("image/png", attachment.Label, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void UpdateFrom_WithEquivalentContents_DoesNotResetContentsBinding()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("same content")],
        }, isInProgress: true);

        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("same content")],
        });

        Assert.DoesNotContain(nameof(ChatHistoryItemViewModel.Contents), changed);
    }

    [AvaloniaFact]
    public void Constructor_RenderableContents_IncludesAllContents()
    {
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3ZfV0AAAAASUVORK5CYII=");
        var call = new FunctionCallContent("web_request", "{\"url\":\"https://example.com\"}");
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextContent("response"),
                new TextReasoningContent("hidden"),
                new DataContent(imageBytes, "image/png"),
                call,
            ],
        });

        Assert.Equal(4, viewModel.RenderableContents.Count);
        Assert.Same(call, viewModel.RenderableContents[3]);
    }

    [AvaloniaFact]
    public async Task UpdateFrom_TestProviderEquivalentAssistantTurn_DoesNotResetContentsBinding()
    {
        var chat = await CreateChatAsync();
        await using var _ = chat;

        chat.EnqueueUserMessage("hello");
        await WaitForConditionAsync(chat.History, () => chat.History.Any(static item => item.Role == ChatRole.Assistant), "assistant history item");
        var assistantHistory = chat.History.LastOrDefault(static item => item.Role == ChatRole.Assistant);

        Assert.NotNull(assistantHistory);
        var viewModel = new ChatHistoryItemViewModel(assistantHistory!);

        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = assistantHistory!.Contents,
        });

        Assert.DoesNotContain(nameof(ChatHistoryItemViewModel.Contents), changed);
        Assert.DoesNotContain(nameof(ChatHistoryItemViewModel.RenderableContents), changed);
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
}
