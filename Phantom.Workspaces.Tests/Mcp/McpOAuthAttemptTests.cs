using System.Net;
using System.Net.Sockets;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Mcp;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed partial class McpOAuthRedirectHandlerTests
{
    [Fact]
    public async Task McpOAuthAttempt_Ready_CompletesBeforeBrowserCanLaunch()
    {
        var browser = new FakeSystemBrowserLauncher();
        var operation = new AttemptOperation();
        await using var attempt = new McpOAuthAttempt(browser, AuthUri("private-state"), operation.RedirectUri, operation);
        browser.OnOpenSideEffect = () =>
        {
            Assert.True(attempt.Ready.IsCompletedSuccessfully);
            Assert.True(attempt.IsListenerBound);
        };
        browser.OnOpen = _ => SendCallbackAsync(operation.RedirectUri, "code=private-code&state=private-state");
        await using var coordinator = new ProviderAcquisitionCoordinator();
        operation.Execute = async (context, cancellationToken) =>
        {
            var callback = await attempt.ExecuteAsync(context.InteractionPermit, cancellationToken);
            Assert.Equal("private-code", GetQueryValue(callback, "code"));
            return await context.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
        };
        Assert.IsType<ProviderAcquisitionSucceeded>(await coordinator.ExecuteOrJoinAsync(
            operation, AttemptPermit(operation), TestContext.Current.CancellationToken));
        await browser.LastCallbackTask!;
        Assert.False(attempt.IsListenerBound);
    }

    [Fact]
    public async Task McpOAuthAttempt_ExecuteAsync_WithoutPermit_DoesNotLaunch()
    {
        var browser = new FakeSystemBrowserLauncher();
        var operation = new AttemptOperation();
        await using var attempt = new McpOAuthAttempt(browser, AuthUri("private-state"), operation.RedirectUri, operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            attempt.ExecuteAsync(null!, TestContext.Current.CancellationToken));
        Assert.Equal(0, browser.OpenCount);
        Assert.False(attempt.IsListenerBound);
    }

    [Fact]
    public async Task McpOAuthAttempt_ExecuteAsync_UnconsumedOrMismatchedProof_DoesNotLaunch()
    {
        var operation = new AttemptOperation();
        foreach (var permit in new[]
        {
            AttemptPermit(operation),
            AttemptPermit(operation, revision: 2).BindExecution(() => true),
            AttemptPermit(operation, generation: Guid.NewGuid()).BindExecution(() => true),
            AttemptPermit(operation, requestIdentifier: new() { Value = Guid.NewGuid() }).BindExecution(() => true),
            AttemptPermit(operation, executor: CredentialExecutorKind.Entra).BindExecution(() => true),
            AttemptPermit(operation, executor: CredentialExecutorKind.OAuth).BindExecution(() => true),
            AttemptPermit(operation, key: operation.CompatibilityKey with
            {
                Source = new() { Digest = new string('c', 64) },
            }).BindExecution(() => true),
            AttemptPermit(operation, key: operation.CompatibilityKey with
            {
                Account = new() { Digest = new string('d', 64) },
            }).BindExecution(() => true),
            AttemptPermit(operation).BindExecution(() => false),
        })
        {
            var browser = new FakeSystemBrowserLauncher();
            await using var attempt = new McpOAuthAttempt(browser, AuthUri("private-state"), AttemptRedirectUri(), operation);
            await Assert.ThrowsAsync<InvalidOperationException>(() => attempt.ExecuteAsync(permit, CancellationToken.None));
            Assert.Equal(0, browser.OpenCount);
        }
    }

    [Fact]
    public async Task McpOAuthAttempt_LateCallbackAfterDispose_IsIgnored()
    {
        var browser = new FakeSystemBrowserLauncher();
        var operation = new AttemptOperation();
        await using var attempt = new McpOAuthAttempt(browser, AuthUri("private-state"), operation.RedirectUri, operation);
        var result = attempt.ExecuteAsync(AttemptPermit(operation).BindExecution(() => true), CancellationToken.None);
        await attempt.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.False(attempt.IsListenerBound);
        using var client = new System.Net.Http.HttpClient();
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => client.GetAsync(
            new Uri(operation.RedirectUri, "?state=private-state&code=late"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            attempt.ExecuteAsync(AttemptPermit(operation).BindExecution(() => true), CancellationToken.None));
        Assert.Equal(1, browser.OpenCount);
    }

    [Fact]
    public async Task McpOAuthAttempt_CancellationAndHandlerShutdown_CloseListeners()
    {
        foreach (var cancelBeforeLaunch in new[] { true, false })
        {
            var browser = new FakeSystemBrowserLauncher();
            using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
            var operation = new AttemptOperation();
            await using var attempt = handler.CreateAttempt(AuthUri("private-state"), operation.RedirectUri, operation);
            using var cancellation = new CancellationTokenSource();
            if (cancelBeforeLaunch)
            {
                cancellation.Cancel();
            }
            var result = attempt.ExecuteAsync(AttemptPermit(operation).BindExecution(() => true), cancellation.Token);
            if (!cancelBeforeLaunch)
            {
                handler.Dispose();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
            Assert.False(attempt.IsListenerBound);
            Assert.Equal(cancelBeforeLaunch ? 0 : 1, browser.OpenCount);
        }
    }

    [Fact]
    public async Task McpOAuthAttempt_CancellationDuringLaunch_TombstonesAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var browser = new FakeSystemBrowserLauncher { OnOpenSideEffect = cancellation.Cancel };
        var operation = new AttemptOperation();
        await using var attempt = new McpOAuthAttempt(browser, AuthUri("private-state"), operation.RedirectUri, operation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            attempt.ExecuteAsync(AttemptPermit(operation).BindExecution(() => true), cancellation.Token));
        Assert.False(attempt.IsListenerBound);
        Assert.Equal(1, browser.OpenCount);
    }

    [Fact]
    public async Task McpOAuthAttempt_ConcurrentExecutions_LaunchOnlyOnce()
    {
        var browser = new FakeSystemBrowserLauncher();
        var operation = new AttemptOperation();
        await using var attempt = new McpOAuthAttempt(browser, AuthUri("private-state"), operation.RedirectUri, operation);
        var proof = AttemptPermit(operation).BindExecution(() => true);
        var first = attempt.ExecuteAsync(proof, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => attempt.ExecuteAsync(proof, CancellationToken.None));
        Assert.Equal(1, browser.OpenCount);
        await attempt.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task McpOAuthAttempt_UnknownCallbackCannotCompleteOrFanOut()
    {
        var firstOperation = new AttemptOperation();
        var secondOperation = new AttemptOperation();
        var browser = new FakeSystemBrowserLauncher();
        await using var first = new McpOAuthAttempt(browser, AuthUri("first-state"), firstOperation.RedirectUri, firstOperation);
        await using var second = new McpOAuthAttempt(browser, AuthUri("second-state"), secondOperation.RedirectUri, secondOperation);
        var firstResult = first.ExecuteAsync(AttemptPermit(firstOperation).BindExecution(() => true), CancellationToken.None);
        var secondResult = second.ExecuteAsync(AttemptPermit(secondOperation).BindExecution(() => true), CancellationToken.None);
        await SendCallbackAsync(firstOperation.RedirectUri, "state=second-state&code=wrong");
        Assert.False(firstResult.IsCompleted);
        Assert.False(secondResult.IsCompleted);
        await SendCallbackAsync(firstOperation.RedirectUri, "state=first-state&code=correct");
        await firstResult;
        Assert.False(secondResult.IsCompleted);
        await second.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondResult);
    }

    [Fact]
    public async Task McpOAuthAttempt_ProviderError_IsSanitized()
    {
        var operation = new AttemptOperation();
        var browser = new FakeSystemBrowserLauncher
        {
            OnOpen = _ => SendCallbackAsync(operation.RedirectUri,
                "state=private-state&error=private-error&error_description=private-description"),
        };
        await using var attempt = new McpOAuthAttempt(browser, AuthUri("private-state"), operation.RedirectUri, operation);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            attempt.ExecuteAsync(AttemptPermit(operation).BindExecution(() => true), CancellationToken.None));
        await browser.LastCallbackTask!;
        Assert.Equal("OAuth authorization failed.", exception.Message);
        Assert.Empty(exception.Data);
        Assert.Null(exception.InnerException);
        Assert.False(attempt.IsListenerBound);
    }

    private static Uri AttemptRedirectUri()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        return new Uri($"http://localhost:{port}/callback");
    }

    private static ProviderInteractionPermit AttemptPermit(
        AttemptOperation operation,
        long? revision = null,
        Guid? generation = null,
        UserAuthorizationRequestIdentifier? requestIdentifier = null,
        CredentialExecutorKind? executor = null,
        ProviderAcquisitionKey? key = null) => new(
            requestIdentifier ?? operation.RequestIdentifier,
            revision ?? operation.Revision,
            executor ?? operation.ExecutorKind,
            generation ?? operation.AcquisitionGeneration,
            key ?? operation.CompatibilityKey,
            () => true);

    private sealed class AttemptOperation : IProviderAcquisitionOperation
    {
        public Uri RedirectUri { get; set; } = AttemptRedirectUri();
        public Func<ProviderAcquisitionCommitContext, CancellationToken, ValueTask<ProviderAcquisitionOutcome>>? Execute { get; set; }
        public ProviderAcquisitionKey CompatibilityKey { get; } = new()
        {
            Configuration = new() { Digest = new string('a', 64) },
            Source = new() { Digest = new string('b', 64) },
            Protocol = ProviderProtocolKind.OAuth,
        };
        public UserAuthorizationRequestIdentifier RequestIdentifier { get; } = new() { Value = Guid.NewGuid() };
        public long Revision => 1;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.McpOAuth;
        public Guid AcquisitionGeneration { get; } = Guid.NewGuid();
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken) => this.Execute!(commitContext, cancellationToken);
    }
}
