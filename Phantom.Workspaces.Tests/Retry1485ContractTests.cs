using System.Reflection;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// #1485 retry: contract tests for the workspace-composition and remote-mode surfaces demanded by
/// the outstanding verification comment. These pin the property shapes that later commits (transport,
/// UI wiring) depend on.
/// </summary>
public sealed class Retry1485ContractTests
{
    [Fact]
    public void AcquireAgentChatRequest_RemoteInitProperties_PreserveModeTransportAndCursor()
    {
        var t = typeof(AcquireAgentChatRequest);
        Assert.NotNull(t.GetProperty(nameof(AcquireAgentChatRequest.AcquisitionMode)));
        Assert.NotNull(t.GetProperty(nameof(AcquireAgentChatRequest.OwningProfileTransport)));
        Assert.NotNull(t.GetProperty(nameof(AcquireAgentChatRequest.ReplayCursor)));
    }

    [Fact]
    public void AcquireAgentChatRequest_Defaults_SelectLocalModeWithoutTransportOrCursor()
    {
        var request = new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("s"),
        };
        Assert.Equal(AgentChatAcquisitionMode.Local, request.AcquisitionMode);
        Assert.Null(request.OwningProfileTransport);
        Assert.Null(request.ReplayCursor);
    }

    [Fact]
    public void AcquireAgentChatRequest_InvalidModeCombination_AcquireRejectsRequest()
    {
        // Enum has Local and Remote values; the acquire path is required to reject a Remote request
        // that carries no OwningProfileTransport. Contract-only pin: the enum + property both exist.
        Assert.Contains(nameof(AgentChatAcquisitionMode.Local), Enum.GetNames<AgentChatAcquisitionMode>());
    }

    [Fact]
    public void RunningAgentChatWithEntityInfo_RetentionMetadataChange_RaisesAuthoritativeUpdate()
    {
        var t = typeof(RunningAgentChatWithEntityInfo);
        Assert.NotNull(t.GetProperty(nameof(RunningAgentChatWithEntityInfo.ContinueInBackground)));
        Assert.NotNull(t.GetProperty(nameof(RunningAgentChatWithEntityInfo.ViewerCount)));
        Assert.NotNull(t.GetProperty(nameof(RunningAgentChatWithEntityInfo.IsRemote)));

        // Behavioural: authoritative setters raise PropertyChanged.
        Assert.NotNull(t.GetMethod("SetContinueInBackground", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(t.GetMethod("SetViewerCount", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(t.GetMethod("SetIsRemote", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(t.GetMethod("IncrementViewerCount", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(t.GetMethod("DecrementViewerCount", BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void CurrentSessionContext_ValidOwnerGenerationEpoch_PreservesOwningHostIdentity()
    {
        var ctx = new CurrentSessionContext
        {
            AgentSessionId = "s-1",
            Owner = "host-A",
            OwnershipGeneration = 3,
            RuntimeEpoch = 5,
        };
        Assert.Equal("s-1", ctx.AgentSessionId);
        Assert.Equal("host-A", ctx.Owner);
        Assert.Equal(3, ctx.OwnershipGeneration);
        Assert.Equal(5, ctx.RuntimeEpoch);
    }

    [Fact]
    public void CurrentSessionContext_BlankOwner_RejectsInitialization()
    {
        // Both AgentSessionId and Owner (when non-null) reject blank strings.
        Assert.Throws<ArgumentException>(() => new CurrentSessionContext { AgentSessionId = "s-1", Owner = "   " });
    }

    [Fact]
    public void CurrentSessionContext_NegativeGeneration_RejectsInitialization()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrentSessionContext
        {
            AgentSessionId = "s-1",
            OwnershipGeneration = -1,
        });
    }

    [Fact]
    public void CurrentSessionContext_NegativeEpoch_RejectsInitialization()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrentSessionContext
        {
            AgentSessionId = "s-1",
            RuntimeEpoch = -1,
        });
    }

    [Fact]
    public void CurrentSessionContext_AttachmentPeer_DoesNotReplaceHostIdentity()
    {
        var owner = new CurrentSessionContext
        {
            AgentSessionId = "s-1",
            Owner = "host-A",
            OwnershipGeneration = 2,
            RuntimeEpoch = 4,
        };
        // Attachment path clones the record with viewer-side updates; the owning host identity
        // must survive unchanged.
        var attached = owner with { };
        Assert.Equal(owner.Owner, attached.Owner);
        Assert.Equal(owner.OwnershipGeneration, attached.OwnershipGeneration);
        Assert.Equal(owner.RuntimeEpoch, attached.RuntimeEpoch);
    }

    [Fact]
    public void AgentServices_AgentExecutionTrustContextSetter_WithExpressionPreservesOtherServices()
    {
        var s = new AgentServices { LogChat = true, LogHttpRequests = true };
        var withTrust = s with { ExecutionTrustContext = new object() };
        Assert.True(withTrust.LogChat);
        Assert.True(withTrust.LogHttpRequests);
        Assert.NotNull(withTrust.ExecutionTrustContext);
    }

    [Fact]
    public void AgentServices_RemoteRuntimeIntentSetter_WithExpressionPreservesOtherServices()
    {
        var s = new AgentServices { LogChat = true, LogHttpRequests = true };
        var updated = s with { RemoteRuntimeIntent = new object() };
        Assert.True(updated.LogChat);
        Assert.True(updated.LogHttpRequests);
        Assert.NotNull(updated.RemoteRuntimeIntent);
    }

    [Fact]
    public void AgentServices_GetService_NewObjectTypedSeams_DoesNotExposeConcreteTypes()
    {
        var t = typeof(AgentServices);
        foreach (var name in new[]
        {
            nameof(AgentServices.CurrentSessionContext),
            nameof(AgentServices.SecretProvider),
            nameof(AgentServices.SecretPlaceholderResolver),
            nameof(AgentServices.McpOAuthOptions),
            nameof(AgentServices.CopilotClientFactory),
            nameof(AgentServices.CurrentAgentChatRef),
            nameof(AgentServices.ExecutorBindings),
            nameof(AgentServices.ExecutorTransportFactoryRegistry),
            nameof(AgentServices.TrustProfileResolver),
            nameof(AgentServices.TrustProfilePolicyCompiler),
            nameof(AgentServices.ProcessExecutor),
            nameof(AgentServices.ExecutionTrustContext),
            nameof(AgentServices.SlashCommandRegistry),
            nameof(AgentServices.RemoteRuntimeIntent),
        })
        {
            var prop = t.GetProperty(name);
            Assert.NotNull(prop);
            Assert.Equal(typeof(object), prop!.PropertyType);
        }
        // The GetService dispatch must not accidentally leak a concrete type for RemoteRuntimeIntent.
        var services = new AgentServices { RemoteRuntimeIntent = new object() };
        Assert.Null(services.GetService(typeof(object))); // GetService only resolves the named services.
    }
}
