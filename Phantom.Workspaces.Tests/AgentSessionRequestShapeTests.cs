using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionRequestShapeTests
{
    [Fact]
    public void Usage_DefaultInitialization_AllOptionalMetricsAreNull()
    {
        var usage = new Usage();
        Assert.Null(usage.TotalInputTokenCount);
        Assert.Null(usage.TotalOutputTokenCount);
        Assert.Null(usage.TotalCacheReadTokenCount);
        Assert.Null(usage.TotalCacheWriteTokenCount);
        Assert.Null(usage.TotalReasoningTokenCount);
        Assert.Null(usage.TotalSessionCostUsd);
    }

    [Fact]
    public void Usage_NamedInitializer_PreservesExactMetricTypes()
    {
        var usage = new Usage
        {
            TotalInputTokenCount = 1L,
            TotalOutputTokenCount = 2L,
            TotalCacheReadTokenCount = 3L,
            TotalCacheWriteTokenCount = 4L,
            TotalReasoningTokenCount = 5L,
            TotalSessionCostUsd = 0.25d,
        };
        Assert.Equal(5L, usage.TotalReasoningTokenCount);
        Assert.Equal(0.25d, usage.TotalSessionCostUsd);
    }

    [Fact]
    public void AgentInformation_RequiredInitProperties_AreMarkedRequired()
    {
        var required = typeof(AgentInformation).GetProperties()
            .Where(property => property.CustomAttributes.Any(
                attribute => attribute.AttributeType == typeof(System.Runtime.CompilerServices.RequiredMemberAttribute)))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(AgentInformation.AgentSessionId), required);
        Assert.Contains(nameof(AgentInformation.AgentDefinition), required);
        Assert.DoesNotContain(nameof(AgentInformation.CurrentModelId), required);
    }

    [Fact]
    public void AgentInformation_NamedInitializer_PreservesAllFields()
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            """{"kind":"prompt","name":"agent","model":{"id":"echo","provider":"echo","apiType":"Echo"}}""");
        var information = new AgentInformation
        {
            AgentSessionId = "session",
            AgentId = "agent",
            Name = "name",
            DisplayName = "Agent",
            Description = "Description",
            AcceptsUserInput = true,
            CurrentModelId = "echo",
            AgentDefinition = definition,
        };
        Assert.Equal("session", information.AgentSessionId);
        Assert.Same(definition, information.AgentDefinition);
        Assert.True(information.AcceptsUserInput);
    }

    [Fact]
    public void OpenAgentSessionHostRequest_NamedInitializer_MapsPeerOpenRequestAndChannel()
    {
        var peer = new TransportPeerIdentity { AuthenticationScheme = "test", StablePeerId = "peer" };
        var open = new AgentSessionOpenRequest
        {
            ProtocolVersion = 1,
            AgentSessionId = "session",
            ExpectedOwningProfileEntityId = Guid.NewGuid().ToString(),
            ExpectedOwnershipGeneration = 1,
            OpenIntent = AgentSessionOpenIntent.Attach,
            AttachmentToken = "attachment",
            Capabilities = [],
        };
        var channel = Moq.Mock.Of<IMessageChannel>();
        var request = new OpenAgentSessionHostRequest { Peer = peer, OpenRequest = open, Channel = channel };
        Assert.Same(peer, request.Peer);
        Assert.Same(open, request.OpenRequest);
        Assert.Same(channel, request.Channel);
    }

    [Fact]
    public void TransportPeerIdentity_BlankAuthenticatedIdentity_RejectsInitialization()
        => Assert.Throws<ArgumentException>(() => new TransportPeerIdentity
        {
            AuthenticationScheme = " ",
            StablePeerId = "peer",
        });

    [Fact]
    public void RuntimeRequestTypes_RequiredMembersDefaultsAndNamedInitializers_MapToLifecycleOperations()
    {
        var epoch = new RuntimeEpoch { Value = Guid.NewGuid() };
        var terminate = new TerminateAgentSessionRuntimeRequest
        {
            SessionId = "session", OwnershipGeneration = 4, Epoch = epoch,
        };
        var retention = new UpdateAgentSessionRuntimeRetentionRequest
        {
            SessionId = "session", OwnershipGeneration = 4, Epoch = epoch, ContinueInBackground = true,
        };
        Assert.Equal(terminate.Epoch, retention.Epoch);
        Assert.True(retention.ContinueInBackground);
    }

    [Fact]
    public void AgentSessionAuthorizationRequest_RequiredMembersDefaultsAndNamedInitializer_MapToAuthorization()
    {
        var request = new AgentSessionAuthorizationRequest
        {
            AgentSessionId = "session",
            ExpectedOwningProfileEntityId = Guid.NewGuid().ToString(),
            ExpectedOwnershipGeneration = 2,
            Operation = AgentSessionAuthorizationOperation.Open,
        };
        Assert.Null(request.ChildAgentId);
        Assert.Equal(AgentSessionAuthorizationOperation.Open, request.Operation);
    }
}
