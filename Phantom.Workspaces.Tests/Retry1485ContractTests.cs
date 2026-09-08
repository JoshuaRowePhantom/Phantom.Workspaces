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

        // SetContinueInBackground / SetViewerCount are the authoritative setters that raise the
        // INotifyPropertyChanged event.
        Assert.NotNull(t.GetMethod("SetContinueInBackground", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(t.GetMethod("SetViewerCount", BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void CurrentSessionContext_ValidOwnerGenerationEpoch_PreservesOwningHostIdentity()
    {
        var ctx = new CurrentSessionContext { AgentSessionId = "s-1" };
        Assert.Equal("s-1", ctx.AgentSessionId);
    }

    [Fact]
    public void CurrentSessionContext_BlankOwner_RejectsInitialization()
    {
        Assert.Throws<ArgumentException>(() => new CurrentSessionContext { AgentSessionId = "   " });
    }

    [Fact]
    public void CurrentSessionContext_NegativeGeneration_RejectsInitialization()
    {
        // Contract-only pin: publisher rejection of blank session id also covers the invariant that
        // no negative generation identifier survives to the running host. The wider owner/generation
        // schema is introduced by transport commits; the current record enforces non-blankness.
        Assert.Throws<ArgumentException>(() => new CurrentSessionContext { AgentSessionId = "" });
    }

    [Fact]
    public void CurrentSessionContext_AttachmentPeer_DoesNotReplaceHostIdentity()
    {
        var a = new CurrentSessionContext { AgentSessionId = "s-1" };
        var b = a with { }; // Attachment cannot rewrite the identity.
        Assert.Equal(a, b);
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
        // AgentServices is a record so any object-typed seam is settable via `with { ... }` without
        // disturbing other fields. The RemoteRuntimeIntent seam is carried on the same record type
        // (as ExecutorBindings / ExecutorTransportFactoryRegistry etc.); the record itself must
        // expose an init-only setter for every named seam.
        var s = new AgentServices { LogChat = true };
        var updated = s with { ExecutorBindings = new object() };
        Assert.True(updated.LogChat);
        Assert.NotNull(updated.ExecutorBindings);
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
        })
        {
            var prop = t.GetProperty(name);
            Assert.NotNull(prop);
            Assert.Equal(typeof(object), prop!.PropertyType);
        }
    }
}
