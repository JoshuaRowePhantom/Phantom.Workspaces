using System.Collections.ObjectModel;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class SubAgentDispatcherSlashCommandTests
{
    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    private static AgentDefinition EchoAgentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);

    private static AgentDefinitionTool Tool(string name, string description) => new()
    {
        Name = name,
        Description = description,
        Definition = EchoAgentDefinition,
    };

    private static async Task<AgentChat> CreateChatAsync(IChatClient clientOverride)
    {
        return await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = clientOverride,
            DisplayNameOverride = "dispatcher",
        });
    }

    [Fact]
    public void ParseNewSubAgent_SplitsDefinitionIdAndPrompt()
    {
        var (definitionId, subAgentId, prompt) = SubAgentSlashCommandParsing.ParseNewSubAgent("foo my-task do the thing");
        Assert.Equal("foo", definitionId);
        Assert.Equal("my-task", subAgentId);
        Assert.Equal("do the thing", prompt);
    }

    [Fact]
    public void ParseNewSubAgent_WithoutPrompt_LeavesPromptEmpty()
    {
        var (definitionId, subAgentId, prompt) = SubAgentSlashCommandParsing.ParseNewSubAgent("foo my-task");
        Assert.Equal("foo", definitionId);
        Assert.Equal("my-task", subAgentId);
        Assert.Equal(string.Empty, prompt);
    }

    [Fact]
    public void ParseSubAgent_SplitsIdAndMessage()
    {
        var (subAgentId, message) = SubAgentSlashCommandParsing.ParseSubAgent("foo-bar hello there");
        Assert.Equal("foo-bar", subAgentId);
        Assert.Equal("hello there", message);
    }

    [Fact]
    public async Task AvailableSubAgents_ListsAllDefinitions()
    {
        var client = new FakeCommandClient
        {
            AvailableDefinitions = [Tool("foo", "A specialized agent for foo tasks."), Tool("bar", "A specialized agent for bar tasks.")],
        };
        var handler = new AvailableSubAgentsSlashCommandHandler(client);
        var chat = await CreateChatAsync(new RecordingChatClient());
        var context = new SlashCommandContext { AgentChat = chat };

        var result = await handler.ExecuteAsync(context, string.Empty, CancellationToken.None);

        Assert.Contains("foo", result.StatusMessage);
        Assert.Contains("A specialized agent for foo tasks.", result.StatusMessage);
        Assert.Contains("bar", result.StatusMessage);
        Assert.Contains("A specialized agent for bar tasks.", result.StatusMessage);
    }

    [Fact]
    public async Task NewSubAgent_Completions_ReturnDefinitionIds()
    {
        var client = new FakeCommandClient
        {
            AvailableDefinitions = [Tool("foo", "foo desc"), Tool("bar", "bar desc")],
        };
        var handler = new NewSubAgentSlashCommandHandler(client);
        var chat = await CreateChatAsync(new RecordingChatClient());
        var context = new SlashCommandContext { AgentChat = chat };

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);

        Assert.Equal(2, completions.Count);
        Assert.Contains(completions, c => c.CompletionText == "foo" && c.Description == "foo desc");
        Assert.Contains(completions, c => c.CompletionText == "bar");

        var filtered = await handler.GetCompletionsAsync(context, "fo", CancellationToken.None);
        var only = Assert.Single(filtered);
        Assert.Equal("foo", only.CompletionText);
    }

    [Fact]
    public async Task NewSubAgent_Execution_EnqueuesEquivalentNewMessage()
    {
        var client = new FakeCommandClient { AvailableDefinitions = [Tool("foo", "foo desc")] };
        var handler = new NewSubAgentSlashCommandHandler(client);
        var recording = new RecordingChatClient();
        var chat = await CreateChatAsync(recording);
        var context = new SlashCommandContext { AgentChat = chat };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await handler.ExecuteAsync(context, "foo my-task hello world", timeout.Token);

        Assert.Contains("my-task", result.StatusMessage);
        var received = await recording.WaitForMessageAsync(timeout.Token);
        Assert.Equal("new(foo my-task): hello world", received);
    }

    [Fact]
    public async Task SubAgent_Completions_ReturnActiveSubAgentIds()
    {
        var client = new FakeCommandClient
        {
            ActiveSubAgents = [new SubAgentDescriptor("foo-bar", "foo desc"), new SubAgentDescriptor("baz-qux", "baz desc")],
        };
        var handler = new SubAgentSlashCommandHandler(client);
        var chat = await CreateChatAsync(new RecordingChatClient());
        var context = new SlashCommandContext { AgentChat = chat };

        var completions = await handler.GetCompletionsAsync(context, string.Empty, CancellationToken.None);
        Assert.Equal(2, completions.Count);
        Assert.Contains(completions, c => c.CompletionText == "foo-bar" && c.Description == "foo desc");

        var filtered = await handler.GetCompletionsAsync(context, "baz", CancellationToken.None);
        var only = Assert.Single(filtered);
        Assert.Equal("baz-qux", only.CompletionText);
    }

    [Fact]
    public async Task SubAgent_Execution_EnqueuesRouteMessage()
    {
        var client = new FakeCommandClient
        {
            ActiveSubAgents = [new SubAgentDescriptor("foo-bar", "foo desc")],
        };
        var handler = new SubAgentSlashCommandHandler(client);
        var recording = new RecordingChatClient();
        var chat = await CreateChatAsync(recording);
        var context = new SlashCommandContext { AgentChat = chat };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await handler.ExecuteAsync(context, "foo-bar follow up message", timeout.Token);

        Assert.Contains("foo-bar", result.StatusMessage);
        var received = await recording.WaitForMessageAsync(timeout.Token);
        Assert.Equal("foo-bar: follow up message", received);
    }

    private sealed class FakeCommandClient : ISubAgentDispatcherCommandClient
    {
        public IReadOnlyList<AgentDefinitionTool> AvailableDefinitions { get; init; } = Array.Empty<AgentDefinitionTool>();

        public IReadOnlyList<SubAgentDescriptor> ActiveSubAgents { get; init; } = Array.Empty<SubAgentDescriptor>();
    }

    /// <summary>
    /// Captures the last user message text the agent chat forwards to the client, so a test can
    /// assert the exact routing message a slash command enqueued.
    /// </summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly TaskCompletionSource<string> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> WaitForMessageAsync(CancellationToken cancellationToken) =>
            _received.Task.WaitAsync(cancellationToken);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Record(messages);
            await Task.CompletedTask;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Record(messages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private void Record(IEnumerable<ChatMessage> messages)
        {
            var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User);
            if (lastUser?.Text is { } text)
            {
                _received.TrySetResult(text);
            }
        }
    }
}
