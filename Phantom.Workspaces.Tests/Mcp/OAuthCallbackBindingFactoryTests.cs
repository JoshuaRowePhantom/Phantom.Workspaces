using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services.Mcp;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed class OAuthCallbackBindingFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Binding_OwnsActualEphemeralPortAndReturnsOnlySafeBrowserResponse(bool mcp)
    {
        var operation = new Operation(mcp ? CredentialExecutorKind.McpOAuth : CredentialExecutorKind.OAuth);
        var factory = new OAuthCallbackBindingFactory();
        await using var binding = await factory.BindAsync(operation, Proof(operation), TestContext.Current.CancellationToken);
        Assert.Equal("127.0.0.1", binding.RedirectUri.Host);
        Assert.True(binding.RedirectUri.Port > 0);
        Assert.True(binding.IsListening);
        var competing = new TcpListener(IPAddress.Loopback, binding.RedirectUri.Port);
        try { Assert.Throws<SocketException>(competing.Start); }
        finally { competing.Stop(); }
        using var client = new HttpClient();
        var responseTask = client.GetAsync(new Uri(binding.RedirectUri, "?code=private-code&state=private-state"), TestContext.Current.CancellationToken);
        var request = await binding.ReceiveAsync();
        Assert.Equal(binding.RedirectUri.AbsolutePath, request.Uri.AbsolutePath);
        Assert.Equal("private-state", System.Web.HttpUtility.ParseQueryString(request.Uri.Query)["state"]);
        Assert.Equal("private-code", System.Web.HttpUtility.ParseQueryString(request.Uri.Query)["code"]);
        await request.CompleteAsync(matched: true);
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("private-", body);
        await binding.DisposeAsync();
        Assert.False(binding.IsListening);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(binding.RedirectUri, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Binding_RejectsUnconsumedAndWrongExecutorProofsBeforeAllocatingListener()
    {
        var operation = new Operation(CredentialExecutorKind.OAuth);
        var factory = new OAuthCallbackBindingFactory();
        var rowPermit = new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision, operation.ExecutorKind,
            operation.AcquisitionGeneration, operation.CompatibilityKey, () => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.BindAsync(operation, rowPermit, TestContext.Current.CancellationToken).AsTask());
        var wrong = new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision, CredentialExecutorKind.McpOAuth,
            operation.AcquisitionGeneration, operation.CompatibilityKey, () => true).BindExecution(() => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.BindAsync(operation, wrong, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Binding_CancellationClosesOwnedServerAndPendingReceive()
    {
        var operation = new Operation(CredentialExecutorKind.OAuth);
        using var cancellation = new CancellationTokenSource();
        await using var binding = await new OAuthCallbackBindingFactory().BindAsync(operation, Proof(operation), cancellation.Token);
        var receive = binding.ReceiveAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        await binding.DisposeAsync();
        Assert.False(binding.IsListening);
    }

    private static ProviderInteractionPermit Proof(Operation operation) => new ProviderInteractionPermit(
        operation.RequestIdentifier, operation.Revision, operation.ExecutorKind,
        operation.AcquisitionGeneration, operation.CompatibilityKey, () => true).BindExecution(() => true);

    private sealed class Operation(CredentialExecutorKind kind) : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey { get; } = new()
        {
            Configuration = new() { Digest = new string('a', 64) },
            Source = new() { Digest = new string('b', 64) },
            Account = new() { Digest = new string('c', 64) },
            Protocol = ProviderProtocolKind.OAuth,
        };
        public UserAuthorizationRequestIdentifier RequestIdentifier { get; } = new() { Value = Guid.NewGuid() };
        public long Revision => 1;
        public CredentialExecutorKind ExecutorKind => kind;
        public Guid AcquisitionGeneration { get; } = Guid.NewGuid();
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(ProviderAcquisitionCommitContext commitContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The binding factory must not execute provider work.");
    }
}
