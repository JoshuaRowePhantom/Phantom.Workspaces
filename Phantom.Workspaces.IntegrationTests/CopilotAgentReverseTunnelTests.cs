using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.IntegrationTests;

/// <summary>
/// Integration test that verifies two in-process instances can collaborate to run a GitHub Copilot
/// agent across a real dev-tunnel reverse connection: a host instance (S) exposes
/// <c>/reverse/connect</c> via the fixture and holds the <see cref="ReverseExecutionRegistry"/>;
/// a client instance (C) connects via the tunnel relay using <see cref="LocalReverseExecutionHandler"/>
/// which drives the real Copilot SDK; the host sends an agent request over the reverse channel and
/// receives a non-empty streamed response.
///
/// All tests skip gracefully when <c>PHANTOM_INTEGRATION_GITHUB_TOKEN</c> is not set.
/// </summary>
[Collection("DevTunnel")]
public sealed class CopilotAgentReverseTunnelTests : IClassFixture<InProcessDevTunnelFixture>
{
    // Minimal Copilot agent definition — provider resolves ${PHANTOM_INTEGRATION_GITHUB_TOKEN} from the
    // environment at execution time inside LocalReverseExecutionHandler → AgentFactory.ResolveApiKey.
    private const string CopilotAgentDefinitionJson = """
        {
            "kind": "prompt",
            "name": "integration-test-agent",
            "model": {
                "id": "gpt-4.1-mini",
                "provider": "github-copilot",
                "apiType": "OpenAI",
                "connection": {
                    "kind": "key",
                    "apiKey": "${PHANTOM_INTEGRATION_GITHUB_TOKEN}"
                }
            },
            "instructions": "You are a helpful assistant. Keep your answers extremely short."
        }
        """;

    private readonly InProcessDevTunnelFixture fixture;

    public CopilotAgentReverseTunnelTests(InProcessDevTunnelFixture fixture)
    {
        this.fixture = fixture;
    }

    [IntegrationFact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task CopilotAgentReverseTunnel_ClientReceivesNonEmptyResponse()
    {
        const string clientId = "integration-copilot-agent";

        // Set up a TCS that completes once the client has registered with the host registry.
        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.fixture.Registry.ConnectionsChanged += (_, _) =>
        {
            if (this.fixture.Registry.IsConnected(clientId))
            {
                connectedTcs.TrySetResult();
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        // Client side: LocalReverseExecutionHandler runs the Copilot agent locally when the host
        // sends an execute frame over the reverse channel.
        var handler = new LocalReverseExecutionHandler();
        var client = ReverseExecutionClientHost.ForEndpoint(
            this.fixture.RelayBaseUri!.ToString(),
            clientId,
            handler,
            this.fixture.AccessToken);

        var runTask = client.RunAsync(cts.Token);

        // Wait until the client has registered with the host registry (event-driven, no Task.Delay).
        await connectedTcs.Task.WaitAsync(cts.Token);
        Assert.True(this.fixture.Registry.TryGetConnection(clientId, out var connection));

        // Host side: send a single-turn agent request to the client over the reverse channel.
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = CopilotAgentDefinitionJson,
            Messages = [new ChatMessage(ChatRole.User, "Reply with the single word: hello")],
        };

        var chunks = new List<string>();
        await foreach (var update in connection.ExecuteAsync(request, cts.Token))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        var responseText = string.Concat(chunks);
        Assert.False(string.IsNullOrWhiteSpace(responseText),
            "Expected a non-empty response from the Copilot agent running on the client instance.");

        cts.Cancel();
        await runTask;
    }
}
