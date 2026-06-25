using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatOutputHtmlModelTests
{
    private sealed record Operation(string Kind, string Path, ChatOutputUpdateLocation Location, string Content);

    private sealed class RecordingSink : IChatOutputHtmlSink
    {
        public List<Operation> Operations { get; } = [];

        public List<Operation> ContentOperations
            => this.Operations.Where(operation => operation.Kind is "update" or "remove").ToList();

        public int ScrollCount => this.Operations.Count(operation => operation.Kind == "scroll");

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Operations.Add(new Operation("update", path, location, content));

        public void RemoveContent(string path)
            => this.Operations.Add(new Operation("remove", path, ChatOutputUpdateLocation.Replace, string.Empty));

        public void ScrollToBottom()
            => this.Operations.Add(new Operation("scroll", string.Empty, ChatOutputUpdateLocation.Replace, string.Empty));

        public void Clear() => this.Operations.Clear();
    }

    private static AgentChatHistoryItem TextMessage(ChatRole role, string text)
        => new() { Role = role, Contents = [new TextContent(text)] };

    [Fact]
    public void InitialHistory_EmitsOneAppendPerMessage_IntoHistoryContainer()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "hello"),
            TextMessage(ChatRole.Assistant, "hi there"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        var appends = sink.ContentOperations;
        Assert.Equal(2, appends.Count);
        Assert.Equal(ChatOutputUpdateLocation.Append, appends[0].Location);
        Assert.Equal(ChatOutputHtmlRenderer.HistoryContainerId, appends[0].Path);
        Assert.Equal(ChatOutputUpdateLocation.After, appends[1].Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), appends[1].Path);
        Assert.Contains("chat-message", appends[0].Content);
        Assert.Contains(">hello<", appends[0].Content);
        Assert.Contains("chat-user-message", appends[0].Content);
        Assert.Contains("chat-assistant-message", appends[1].Content);
        Assert.True(sink.ScrollCount >= 1);
    }

    [Fact]
    public void AddingMessage_AppendsAfterPreviousMessage()
    {
        var history = new ObservableCollection<AgentChatHistoryItem> { TextMessage(ChatRole.User, "first") };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        history.Add(TextMessage(ChatRole.Assistant, "second"));

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.After, operation.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), operation.Path);
        Assert.Contains(">second<", operation.Content);
    }

    [Fact]
    public void StreamingUpdate_WhenLeadingContentUnchanged_OnlyEmitsForChangedContent()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("stable")] },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        // Replace the message with one that keeps the leading content and appends a new block.
        history[0] = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("stable"), new TextContent("appended")],
        };

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.Append, operation.Location);
        Assert.Equal(ChatOutputHtmlRenderer.ContentsContainerId(ChatOutputHtmlRenderer.MessageId(0)), operation.Path);
        Assert.Contains(">appended<", operation.Content);
    }

    [Fact]
    public void StreamingUpdate_WhenLastContentChanges_ReplacesThatContentById()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("partial")] },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        history[0] = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("partial complete")],
        };

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.Replace, operation.Location);
        Assert.Equal(ChatOutputHtmlRenderer.ContentId(ChatOutputHtmlRenderer.MessageId(0), 0), operation.Path);
        Assert.Contains("partial complete", operation.Content);
    }

    [Fact]
    public void RemovingMessage_EmitsRemoveByElementId()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "keep"),
            TextMessage(ChatRole.Assistant, "drop"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        history.RemoveAt(1);

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal("remove", operation.Kind);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(1), operation.Path);
    }

    [Fact]
    public void ReasoningHidden_DoesNotRenderReasoningContent_UntilToggledOn()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new TextReasoningContent("thinking"), new TextContent("answer")],
            },
        };
        var sink = new RecordingSink();
        var reasoningVisible = false;
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => reasoningVisible, sink);

        var initial = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("thinking", initial.Content);
        Assert.Contains("answer", initial.Content);

        sink.Clear();
        reasoningVisible = true;
        model.Refresh();

        Assert.Contains(sink.ContentOperations, operation => operation.Content.Contains("thinking"));
    }

    [Fact]
    public void RunningItem_RendersContainerThenAppendsMessagesIntoIt()
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "working"));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);

        var operations = sink.ContentOperations;
        Assert.Equal(2, operations.Count);

        // First the empty running container appended into the running region.
        Assert.Equal(ChatOutputHtmlRenderer.RunningContainerId, operations[0].Path);
        Assert.Contains(ChatOutputHtmlRenderer.RunningItemId(0), operations[0].Content);

        // Then the message appended into that container.
        Assert.Equal(ChatOutputHtmlRenderer.RunningItemContentsId(ChatOutputHtmlRenderer.RunningItemId(0)), operations[1].Path);
        Assert.Contains(">working<", operations[1].Content);
    }

    [Fact]
    public void HtmlEscape_EscapesMarkupInMessageText()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "<script>alert('x')</script>"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        var operation = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("<script>", operation.Content);
        Assert.Contains("&lt;script&gt;", operation.Content);
    }
}
