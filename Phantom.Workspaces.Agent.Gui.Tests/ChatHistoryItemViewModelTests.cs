using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatHistoryItemViewModelTests
{
    [Fact]
    public void Constructor_UserRole_MapsToUserLabel()
    {
        var source = new AgentChatHistoryItem
        {
            Role = ChatRole.User,
            Text = "hello",
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
            Text = "response",
            ReasoningText = "hidden reasoning",
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
            Text = "streaming",
            IsInProgress = true,
        });

        viewModel.UpdateFrom(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Text = "complete",
            ReasoningText = "because",
            IsInProgress = false,
        });

        Assert.Equal("complete", viewModel.Text);
        Assert.False(viewModel.IsInProgress);
        Assert.Empty(viewModel.ReasoningDisplayText);
    }

    [Fact]
    public void SetReasoningVisible_ShowsReasoningText()
    {
        var viewModel = new ChatHistoryItemViewModel(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Text = "answer",
            ReasoningText = "step 1",
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
            Text = "answer",
            ReasoningText = "step 1",
            IsInProgress = false,
        });

        viewModel.SetReasoningVisible(true);

        Assert.Equal("step 1", viewModel.ReasoningDisplayText);
        Assert.True(viewModel.HasReasoningLine);
    }
}
