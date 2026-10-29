using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Core.Tests.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests;

internal sealed class McpCredentialTestEnvironment : IAccessBroker
{
    internal const string Material = "fixture-credential-value";
    internal Func<StoredSecretAccessRequest, CancellationToken, Task<AccessOutcome<StoredSecretAccessResult>>>? Acquire { get; set; }
    internal ConcurrentQueue<StoredSecretAccessRequest> Requests { get; } = new();
    internal ConcurrentQueue<StoredSecretAccessResult> Results { get; } = new();
    internal ConcurrentDictionary<string, int> HttpRequests { get; } = new();
    internal ConcurrentDictionary<string, int> TransportDisposals { get; } = new();
    internal bool RejectOptionalRequests { get; set; }

    internal AgentServices Services(DeterministicTestChatClient client) => new()
    {
        AccessBroker = this,
        ChatClientOverride = client,
        McpHttpMessageHandlerFactory = () => new ServerHandler(this),
    };

    public async Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default)
        where TResult : IAccessResult
    {
        var stored = Assert.IsType<StoredSecretAccessRequest>(request);
        this.Requests.Enqueue(stored);
        var outcome = this.Acquire is { } acquire
            ? await acquire(stored, cancellationToken)
            : this.Success();
        return (AccessOutcome<TResult>)(object)outcome;
    }

    internal AccessOutcome<StoredSecretAccessResult> Success()
    {
        var result = ManifestAccessTestBroker.CreateResult(Material);
        this.Results.Enqueue(result);
        return new AccessSucceeded<StoredSecretAccessResult> { Result = result };
    }

    internal static AccessOutcome<StoredSecretAccessResult> Failure(string outcome) => outcome switch
    {
        "denied" => new AccessDenied<StoredSecretAccessResult> { Reason = AccessDenialReason.UserDenied },
        "cancelled" => new AccessCancelled<StoredSecretAccessResult>(),
        "reacquisition" => new AccessReacquisitionRequired<StoredSecretAccessResult>
        {
            RequestIdentifier = new() { Value = Guid.NewGuid() },
        },
        "authorization" => new AccessAuthorizationRequired<StoredSecretAccessResult>
        {
            RequestIdentifier = new() { Value = Guid.NewGuid() },
        },
        _ => new AccessFailed<StoredSecretAccessResult>
        {
            ErrorCode = AccessErrorCode.ProviderFailure,
            SafeMessage = outcome == "missing" ? "Credential unavailable." : "Provider failed.",
            CanRetry = false,
        },
    };

    internal static AgentDefinition Definition(bool required = false, bool sibling = false, bool anonymousSibling = false)
    {
        var definition = System.Text.Json.Nodes.JsonNode.Parse("""
        {
          "kind":"prompt", "name":"credential-isolation",
          "model":{"id":"echo","provider":"echo","apiType":"Echo"},
          "tools":[
            {"kind":"mcp","name":"optional","serverName":"optional",
             "connection":{"kind":"key","endpoint":"https://optional.test/mcp","apiKey":"${SECRET:Optional}"},
             "approvalMode":{"kind":"never"}}
          ]
        }
        """)!;
        if (required)
            definition["model"]!["connection"] = System.Text.Json.Nodes.JsonNode.Parse(
                """{"kind":"key","apiKey":"${SECRET:Required}"}""");
        if (sibling)
            definition["tools"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(
                """{"kind":"mcp","name":"healthy","serverName":"healthy","connection":{"kind":"key","endpoint":"https://healthy.test/mcp","apiKey":"${SECRET:Healthy}"},"approvalMode":{"kind":"never"}}"""));
        if (anonymousSibling)
              definition["tools"]![1]!["connection"] = System.Text.Json.Nodes.JsonNode.Parse(
                  """{"kind":"Anonymous","endpoint":"https://healthy.test/mcp"}""");
        return AgentDefinitionLoader.LoadAgentFromJson(definition.ToJsonString());
    }

    internal static DeterministicTestChatClient Client()
    {
        var client = new DeterministicTestChatClient();
        QueueAnswer(client);
        return client;
    }

    internal static void QueueAnswer(DeterministicTestChatClient client)
    {
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "answer") { FinishReason = ChatFinishReason.Stop });
        stream.Complete();
    }

    internal static async Task AssertTurnCompletesAsync(AgentChat chat)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs change)
        {
            if (change.NewItems?.OfType<AgentChatHistoryItem>().Any(item => item.Role == ChatRole.Assistant) == true)
                answered.TrySetResult();
        }
        ((INotifyCollectionChanged)chat.History).CollectionChanged += OnHistoryChanged;
        try
        {
            chat.EnqueueUserMessage("Please answer.");
            await answered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { ((INotifyCollectionChanged)chat.History).CollectionChanged -= OnHistoryChanged; }
    }

    internal static async Task AssertUsableUnavailableAsync(
        AgentChat chat,
        DeterministicTestChatClient client,
        McpCredentialTestEnvironment environment,
        int expectedRequests = 1)
    {
        await chat.HistoryPopulated;
        await chat.Initialization;
        var server = Assert.Single(chat.GetToolSnapshot());
        Assert.Equal("optional", server.Name);
        Assert.False(server.IsEnabled);
        Assert.Empty(server.Children);
        Assert.Contains("Failed to open MCP server", server.Status, StringComparison.Ordinal);
        await AssertTurnCompletesAsync(chat);
        Assert.Empty(client.LastRequestOptions?.Tools ?? []);
        Assert.Empty(environment.HttpRequests);
        Assert.Equal(expectedRequests, environment.Requests.Count);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ServerHandler(McpCredentialTestEnvironment owner) : HttpMessageHandler
    {
        private string? host;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.host = request.RequestUri!.Host;
            owner.HttpRequests.AddOrUpdate(this.host, 1, (_, value) => value + 1);
            Assert.Equal(Material, request.Headers.Authorization?.Parameter);
            if (owner.RejectOptionalRequests && this.host == "optional.test")
                throw new InvalidOperationException("Synthetic connection failure.");
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.MethodNotAllowed);
            if (request.Method == HttpMethod.Delete)
                return new(HttpStatusCode.NoContent);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("id", out var identifier))
                return new(HttpStatusCode.Accepted);
            var method = document.RootElement.GetProperty("method").GetString();
            var result = method switch
            {
                "tools/list" => $$$$"""{"tools":[{"name":"{{{{this.host.Split('.')[0]}}}}_tool","description":"Fixture tool","inputSchema":{"type":"object","properties":{}}}]}""",
                "tools/call" => """{"content":[{"type":"text","text":"fixture result"}]}""",
                _ => """{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"}}""",
            };
            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    $$"""{"jsonrpc":"2.0","id":{{identifier.GetRawText()}},"result":{{result}}}""",
                    Encoding.UTF8, "application/json"),
            };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && this.host is { } name)
                owner.TransportDisposals.AddOrUpdate(name, 1, (_, value) => value + 1);
            base.Dispose(disposing);
        }
    }
}
