using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ReverseFrameSerializationTests
{
    private static readonly JsonSerializerOptions Options = AIJsonUtilities.DefaultOptions;

    [Fact]
    public void ExecuteFrame_RoundTrips_WithRequestAndMessages()
    {
        var frame = new ReverseFrame
        {
            Type = ReverseFrame.Types.Execute,
            CorrelationId = "abc",
            Request = new RemoteAgentRequest
            {
                AgentDefinitionJson = "{\"kind\":\"prompt\"}",
                AgentSessionId = "session-1",
                Messages = [new ChatMessage(ChatRole.User, "hello")],
            },
        };

        var json = JsonSerializer.Serialize(frame, Options);
        var roundTrip = JsonSerializer.Deserialize<ReverseFrame>(json, Options)!;

        Assert.Equal(ReverseFrame.Types.Execute, roundTrip.Type);
        Assert.Equal("abc", roundTrip.CorrelationId);
        Assert.Equal("session-1", roundTrip.Request!.AgentSessionId);
        Assert.Equal("hello", roundTrip.Request!.Messages.Single().Text);
    }

    [Fact]
    public void UpdateFrame_RoundTrips_WithChatResponseUpdate()
    {
        var frame = new ReverseFrame
        {
            Type = ReverseFrame.Types.Update,
            CorrelationId = "abc",
            Update = new ChatResponseUpdate(ChatRole.Assistant, "partial text"),
        };

        var json = JsonSerializer.Serialize(frame, Options);
        var roundTrip = JsonSerializer.Deserialize<ReverseFrame>(json, Options)!;

        Assert.Equal("partial text", roundTrip.Update!.Text);
    }

    [Fact]
    public void RegisterFrame_RoundTrips()
    {
        var frame = new ReverseFrame
        {
            Type = ReverseFrame.Types.Register,
            ClientInstanceId = "computer-a",
            AcceptedAgentDefinitionNames = ["a", "b"],
        };

        var json = JsonSerializer.Serialize(frame, Options);
        var roundTrip = JsonSerializer.Deserialize<ReverseFrame>(json, Options)!;

        Assert.Equal("computer-a", roundTrip.ClientInstanceId);
        Assert.Equal(["a", "b"], roundTrip.AcceptedAgentDefinitionNames);
    }

    [Fact]
    public void CompleteFrame_RoundTrips_WithError()
    {
        var frame = new ReverseFrame
        {
            Type = ReverseFrame.Types.Complete,
            CorrelationId = "abc",
            Error = new ReverseExecutionError("execution-failed", "boom"),
        };

        var json = JsonSerializer.Serialize(frame, Options);
        var roundTrip = JsonSerializer.Deserialize<ReverseFrame>(json, Options)!;

        Assert.Equal("execution-failed", roundTrip.Error!.Code);
        Assert.Equal("boom", roundTrip.Error!.Message);
    }
}
