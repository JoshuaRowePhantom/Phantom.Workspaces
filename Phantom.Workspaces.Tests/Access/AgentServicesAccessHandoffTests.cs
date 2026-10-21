using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class AgentServicesAccessHandoffTests
{
    [Fact]
    public void AgentServices_TypedAccessBoundary_ReturnsExactBrokerAndActualCatalog()
    {
        var broker = new MetadataBroker();
        var catalog = new MetadataCatalog();
        var context = StoredSecretAccessExecutorTests.Request();
        var services = new AgentServices
        {
            AccessBroker = broker,
            McpOAuthCredentials = catalog,
            AccessRequestor = context.Requestor,
            AccessUse = context.Use,
            AccessLineage = context.Use.Lineage,
            AccessPresentationScope = AccessPresentationScope.Bootstrap,
        };

        Assert.Same(broker, services.AccessBroker);
        Assert.Same(broker, services.GetService(typeof(IAccessBroker)));
        Assert.Same(catalog, services.McpOAuthCredentials);
        Assert.Same(catalog, services.GetService(typeof(IMcpOAuthCredentialRequestCatalog)));
        Assert.Same(context.Requestor, services.AccessRequestor);
        Assert.Same(context.Use, services.AccessUse);
        Assert.Same(context.Use.Lineage, services.AccessLineage);
        Assert.Equal(AccessPresentationScope.Bootstrap, services.AccessPresentationScope);
        Assert.Null(services.GetService(typeof(object)));
    }

    [Fact]
    public void AgentServices_UnconfiguredAccessBoundary_DoesNotFabricateServices()
    {
        var services = new AgentServices();
        Assert.Null(services.AccessBroker);
        Assert.Null(services.McpOAuthCredentials);
        Assert.Null(services.GetService(typeof(IAccessBroker)));
        Assert.Null(services.GetService(typeof(IMcpOAuthCredentialRequestCatalog)));
        Assert.Equal(AccessPresentationScope.Runtime, services.AccessPresentationScope);
    }

    private sealed class MetadataBroker : IAccessBroker
    {
        public Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
            AccessRequest<TResult> request,
            CancellationToken cancellationToken) where TResult : IAccessResult =>
            throw new InvalidOperationException("Service lookup must not acquire credentials.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MetadataCatalog : IMcpOAuthCredentialRequestCatalog
    {
        public McpOAuthCredentialUseRequest? CreateRequest(
            McpTool tool,
            AccessRequestor requestor,
            CredentialUseDescriptor use,
            AccessPresentationScope presentationScope) =>
            throw new InvalidOperationException("Service lookup must not execute a metadata factory.");
    }
}
