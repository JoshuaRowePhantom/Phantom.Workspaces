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

    [Fact]
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

    [Fact]
    public void Constructor_AssistantRole_MapsToAssistantLabel()
    {
        var source = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("response"), new TextReasoningContent("hidden reasoning")],
            IsInProgress = true,
        };

        var viewModel = new ChatHistoryItemViewModel(source);

        Assert.False(viewModel.IsUser);
        Assert.Equal("assistant", viewModel.RoleLabel);
        Assert.Equal("response", viewModel.Text);
        Assert.Equal("Thinking ...", viewModel.ReasoningDisplayText);
        Assert.True(viewModel.HasReasoningLine);
    }

    [Fact]
    public void UpdateFrom_UpdatesTextAndProgressState()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streaming")],
            IsInProgress = true,
        });

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("complete"), new TextReasoningContent("because")],
            IsInProgress = false,
        });

        Assert.Equal("complete", viewModel.Text);
        Assert.False(viewModel.IsInProgress);
        Assert.Empty(viewModel.ReasoningDisplayText);
    }

    [Fact]
    public void UpdateFrom_CompletingResponse_RefreshesReasoningVisibility()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streaming")],
            IsInProgress = true,
        });

        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("complete"), new TextReasoningContent("because")],
            IsInProgress = false,
        });

        Assert.Contains(nameof(ChatHistoryItemViewModel.IsInProgress), changed);
        Assert.Contains(nameof(ChatHistoryItemViewModel.HasReasoningLine), changed);
        Assert.Contains(nameof(ChatHistoryItemViewModel.ReasoningDisplayText), changed);
        Assert.False(viewModel.HasReasoningLine);
        Assert.Empty(viewModel.ReasoningDisplayText);
    }

    [Fact]
    public void SetReasoningVisible_ShowsReasoningText()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("answer"), new TextReasoningContent("step 1")],
            IsInProgress = true,
        });

        viewModel.SetReasoningVisible(true);

        Assert.Equal("step 1", viewModel.ReasoningDisplayText);
    }

    [Fact]
    public void SetReasoningVisible_WhenNotInProgress_ShowsReasoningText()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("answer"), new TextReasoningContent("step 1")],
            IsInProgress = false,
        });

        viewModel.SetReasoningVisible(true);

        Assert.Equal("step 1", viewModel.ReasoningDisplayText);
        Assert.True(viewModel.HasReasoningLine);
    }

    [Fact]
    public void Constructor_WithImageContent_ExposesAttachmentPreview()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Contents = [new DataContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3ZfV0AAAAASUVORK5CYII="), "image/png")],
        });

        var attachment = Assert.Single(viewModel.Attachments);

        Assert.True(viewModel.HasAttachments);
        Assert.Equal("image/png", attachment.Label);
    }

    [Fact]
    public void UpdateFrom_WithEquivalentContents_DoesNotResetContentsBinding()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("same content")],
            IsInProgress = true,
        });

        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("same content")],
            IsInProgress = false,
        });

        Assert.DoesNotContain(nameof(ChatHistoryItemViewModel.Contents), changed);
    }

    [Fact]
    public void Constructor_RenderableContents_FiltersReasoningAndImageData()
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

        Assert.Collection(
            viewModel.RenderableContents,
            content => Assert.IsType<TextContent>(content),
            content => Assert.Same(call, content));
    }

    [Fact]
    public async Task UpdateFrom_TestProviderEquivalentAssistantTurn_DoesNotResetContentsBinding()
    {
        var chat = AgentFactory.CreateAgentChat(CreateTestProviderAgentDefinition());
        await using var _ = chat;

        chat.EnqueueUserMessage("hello");

        AgentChatHistoryItem? assistantHistory = null;
        for (var i = 0; i < 20; i++)
        {
            assistantHistory = chat.History.LastOrDefault(static item => item.Role == ChatRole.Assistant);
            if (assistantHistory is not null)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(assistantHistory);
        var viewModel = new ChatHistoryItemViewModel(assistantHistory!);

        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = assistantHistory!.Contents,
            IsInProgress = assistantHistory.IsInProgress,
        });

        Assert.DoesNotContain(nameof(ChatHistoryItemViewModel.Contents), changed);
        Assert.DoesNotContain(nameof(ChatHistoryItemViewModel.RenderableContents), changed);
    }
}
