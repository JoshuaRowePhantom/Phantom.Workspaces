using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class AgentRespondHandlerTests
{
    private const string EchoAgentJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    [Fact]
    public async Task RespondAsync_EchoAgent_ReturnsEchoedText()
    {
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            Messages = [new ChatMessage(ChatRole.User, "hello-remote")],
        };

        var response = await AgentRespondHandler.RespondAsync(request);

        Assert.Equal("hello-remote", response.Text);
    }

    [Fact]
    public async Task RespondAsync_WhenTargetInstanceSet_RunsLocally()
    {
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            TargetClientInstance = "not-connected",
            Messages = [new ChatMessage(ChatRole.User, "hello-local")],
        };

        var response = await AgentRespondHandler.RespondAsync(request);

        Assert.Equal("hello-local", response.Text);
    }

    [Fact]
    public void RemoteAgentRequest_RoundTrips_WithAiJsonOptions()
    {
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = "session-1",
            TargetClientInstance = "computer-a",
            Messages = [new ChatMessage(ChatRole.User, "ping")],
        };

        var json = JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions);
        var roundTripped = JsonSerializer.Deserialize<RemoteAgentRequest>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal("session-1", roundTripped!.AgentSessionId);
        Assert.Equal("computer-a", roundTripped.TargetClientInstance);
        Assert.Single(roundTripped.Messages);
        Assert.Equal("ping", roundTripped.Messages[0].Text);
    }
}
