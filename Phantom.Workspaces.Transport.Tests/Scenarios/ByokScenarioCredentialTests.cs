using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Tests;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

public sealed class ByokScenarioCredentialTests
{
    [Fact]
    public async Task AsyncFactory_BindsTypedReferenceAndIndependentlyOwnedResults()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var server = new ScriptedByokChatServer();
        await using var credentials = new ByokScenarioCredentials();
        var definition = ByokScenarioCredentials.Definition(server.BaseUrl);
        var original = definition.ToJson();
        var first = (await AgentFactory.CreateChatClientAsync(
            definition, credentials.Services, cancellationToken: ct)).ChatClient;
        await using var firstLifetime = Assert.IsAssignableFrom<IAsyncDisposable>(first);
        var second = await credentials.CreateExecutorAsync(server.BaseUrl, ct);
        await using var secondLifetime = Assert.IsAssignableFrom<IAsyncDisposable>(second);

        Assert.Equal(2, credentials.Requests.Count);
        Assert.All(credentials.Requests, request =>
        {
            Assert.Equal(ByokScenarioCredentials.KeyName, request.Reference.Name);
            Assert.Equal(credentials.Services.AccessRequestor, request.Requestor);
            Assert.Equal(AccessPresentationScope.Bootstrap, request.PresentationScope);
            Assert.Equal(CredentialProviderKind.StoredSecret, request.Credential.Provider);
            Assert.Equal(CredentialKind.Secret, request.Credential.Kind);
            Assert.Equal(CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
            {
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialKind.Secret,
                CredentialPartition = ByokScenarioCredentials.KeyName,
                SourceCompatibility = "environment",
                Authority = "",
                Audience = server.BaseUrl,
                ClientPartition = "github-models",
                Scopes = [],
            }), request.Credential.LogicalIdentifier);
            var source = Assert.Single(request.Sources);
            Assert.Equal(CredentialSourceKind.Environment, source.Kind);
            Assert.Equal(CredentialProviderKind.StoredSecret, source.Provider);
            Assert.Equal(CredentialIdentityFactory.CreateSourceIdentifier(
                CredentialProviderKind.StoredSecret, $"Environment:{ByokScenarioCredentials.KeyName}"), source.Identifier);
            var lineage = credentials.Services.AccessLineage! with
            {
                Key = CredentialIdentityFactory.CreateLineageIdentifier("secret-key", ByokScenarioCredentials.KeyName),
            };
            Assert.Equal(lineage, request.Use.Lineage);
            Assert.Equal(CredentialIdentityFactory.CreateUseFingerprint(
                CredentialUseKind.Agent, lineage, request.Requestor.Identifier,
                $"model:github-models:gpt-test:{server.BaseUrl}"), request.Use.Fingerprint);
        });
        Assert.NotSame(credentials.Results[0], credentials.Results[1]);
        Assert.NotSame(credentials.Results[0].Lease, credentials.Results[1].Lease);
        Assert.All(credentials.Bindings, binding => Assert.Equal(credentials.Generation, binding.MaterialGeneration));
        Assert.Equal(2, credentials.Validations);
        Assert.Equal(2, credentials.GenerationChecks);

        await Assert.IsAssignableFrom<IAsyncDisposable>(first).DisposeAsync();
        var disposed = await credentials.Results[0].Lease.UseAsync((_, _) => ValueTask.FromResult(true), ct);
        Assert.Equal(AccessErrorCode.Disposed, Assert.IsType<SensitiveValueUseFailed<bool>>(disposed).ErrorCode);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => first.GetResponseAsync([new ChatMessage(ChatRole.User, "disposed")], cancellationToken: ct));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => TransportScenarioSupport.RunTurnAsync(first, "disposed", ct));
        Assert.Empty(server.RecordedRequests);
        Assert.Equal(2, credentials.Requests.Count);

        var before = credentials.Validations;
        var generationsBefore = credentials.GenerationChecks;
        var conversation = server.AddConversation("independent", _ =>
        {
            Assert.True(credentials.Validations > before);
            Assert.True(credentials.GenerationChecks > generationsBefore);
            return true;
        });
        var stream = conversation.Client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "independent-pong"));
        stream.Complete();
        var text = await TransportScenarioSupport.RunTurnAsync(second, "independent-marker", ct);
        Assert.Contains("independent-pong", text, StringComparison.Ordinal);
        Assert.Single(server.RecordedRequests);
        Assert.Empty(server.Failures);
        Assert.Equal(2, credentials.Requests.Count);
        Assert.Equal(original, definition.ToJson());
        Assert.DoesNotContain("scenario-credential", definition.ToJson(), StringComparison.Ordinal);

        await Assert.IsAssignableFrom<IAsyncDisposable>(second).DisposeAsync();
        var secondDisposed = await credentials.Results[1].Lease.UseAsync((_, _) => ValueTask.FromResult(true), ct);
        Assert.Equal(AccessErrorCode.Disposed, Assert.IsType<SensitiveValueUseFailed<bool>>(secondDisposed).ErrorCode);
    }

    [Fact]
    public async Task AcquisitionRefused_NoClientOrHttpRequest()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var server = new ScriptedByokChatServer();
        await using var credentials = new ByokScenarioCredentials { Authorized = false };
        var refusal = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => credentials.CreateExecutorAsync(server.BaseUrl, ct));
        Assert.Equal("Credential authorization is required.", refusal.Message);
        Assert.Single(credentials.Requests);
        Assert.Empty(credentials.Results);
        Assert.Equal(0, credentials.Validations);
        Assert.Empty(server.RecordedRequests);
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgottenOrReplacedLease_RefusesBothTurnSurfacesWithoutReacquiring(bool replaced)
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var server = new ScriptedByokChatServer();
        await using var credentials = new ByokScenarioCredentials();
        var executor = await credentials.CreateExecutorAsync(server.BaseUrl, ct);
        await using var executorLifetime = Assert.IsAssignableFrom<IAsyncDisposable>(executor);
        if (replaced)
        {
            credentials.Generation = new() { Value = Guid.NewGuid() };
        }
        else
        {
            credentials.Authorized = false;
        }

        var responseRefusal = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => executor.GetResponseAsync([new ChatMessage(ChatRole.User, "refused")], cancellationToken: ct));
        var streamRefusal = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => TransportScenarioSupport.RunTurnAsync(executor, "refused", ct));
        var expectedMessage = replaced ? "Credential reacquisition is required." : "Credential authorization is required.";
        Assert.Equal(expectedMessage, responseRefusal.Message);
        Assert.Equal(expectedMessage, streamRefusal.Message);
        Assert.Equal(2, credentials.RequiredActions.Count);
        Assert.All(credentials.RequiredActions, action => Assert.Equal(
            replaced ? CredentialLeaseRequiredActionKind.Reacquisition : CredentialLeaseRequiredActionKind.Authorization, action));
        Assert.Single(credentials.Requests);
        Assert.Single(credentials.Results);
        Assert.Empty(server.RecordedRequests);
        Assert.Empty(server.Failures);
        await Assert.IsAssignableFrom<IAsyncDisposable>(executor).DisposeAsync();
    }

    [Theory]
    [InlineData(ByokScenarioCredentials.Reference)]
    [InlineData("obsolete-inline-key")]
    public async Task SynchronousCredentialedFactory_StillRejected(string reference)
    {
        await using var server = new ScriptedByokChatServer();
        await using var credentials = new ByokScenarioCredentials();
        var refusal = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateChatClient(
            ByokScenarioCredentials.Definition(server.BaseUrl, reference), credentials.Services));
        Assert.Equal("Credentialed client construction requires CreateChatClientAsync.", refusal.Message);
        Assert.Empty(credentials.Requests);
        Assert.Empty(server.RecordedRequests);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task AwaitOnlyInlineMigration_StillRejectedWithoutBrokerOrHttpUse()
    {
        await using var server = new ScriptedByokChatServer();
        await using var credentials = new ByokScenarioCredentials();
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentFactory.CreateChatClientAsync(
            ByokScenarioCredentials.Definition(server.BaseUrl, "obsolete-inline-key"), credentials.Services));
        Assert.Equal("Inline model credentials are not supported. Use a secret or environment reference.", refusal.Message);
        Assert.Empty(credentials.Requests);
        Assert.Empty(server.RecordedRequests);
        Assert.Empty(server.Failures);
    }
}
