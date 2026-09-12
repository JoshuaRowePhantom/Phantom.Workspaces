using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentExecutionTrustContextTests
{
    [Fact]
    public async Task GetCompilationAsync_ConcurrentCallers_ResolveAndCompileOnce()
    {
        var resolutionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResolution = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingResolver(
            async cancellationToken =>
            {
                resolutionStarted.SetResult();
                await allowResolution.Task.WaitAsync(cancellationToken);
                return new RemoteTrustProfileResolution(new TrustProfile(), "7");
            });
        var compiler = new RecordingCompiler(
            new TrustProfileProcessPolicyCompilation(false, null, []));
        var context = new AgentExecutionTrustContext(
            new AgentExecutionTrustProfileReference("trust-profile", "restricted", "7"),
            resolver,
            compiler);

        var first = context.GetCompilationAsync().AsTask();
        await resolutionStarted.Task;
        var second = context.GetCompilationAsync().AsTask();
        allowResolution.SetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Same(results[0], results[1]);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, compiler.CallCount);
    }

    [Fact]
    public async Task GetCompilationAsync_StaleRevision_CachesFailClosedResult()
    {
        var resolver = new RecordingResolver(
            _ => Task.FromResult<RemoteTrustProfileResolution?>(
                new(new TrustProfile(), "8")));
        var compiler = new RecordingCompiler(
            new TrustProfileProcessPolicyCompilation(false, null, []));
        var context = new AgentExecutionTrustContext(
            new AgentExecutionTrustProfileReference("trust-profile", "restricted", "7"),
            resolver,
            compiler);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.GetCompilationAsync());
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.GetCompilationAsync());

        Assert.Contains("changed", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, compiler.CallCount);
    }

    [Fact]
    public async Task GetCompilationAsync_CancelledWait_DoesNotCancelSharedCompilation()
    {
        var resolutionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResolution = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingResolver(
            async cancellationToken =>
            {
                resolutionStarted.SetResult();
                await allowResolution.Task.WaitAsync(cancellationToken);
                return new RemoteTrustProfileResolution(new TrustProfile(), "7");
            });
        var compiler = new RecordingCompiler(
            new TrustProfileProcessPolicyCompilation(false, null, []));
        var context = new AgentExecutionTrustContext(
            new AgentExecutionTrustProfileReference("trust-profile", "restricted", "7"),
            resolver,
            compiler);
        using var cancellation = new CancellationTokenSource();

        var cancelledWait = context.GetCompilationAsync(cancellation.Token).AsTask();
        await resolutionStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);

        allowResolution.SetResult();
        var result = await context.GetCompilationAsync();
        Assert.False(result.RequiresContainment);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, compiler.CallCount);
    }

    [Fact]
    public async Task GetCompilationAsync_MissingProfile_CachesFailClosedResult()
    {
        var resolver = new RecordingResolver(
            _ => Task.FromResult<RemoteTrustProfileResolution?>(null));
        var context = new AgentExecutionTrustContext(
            new AgentExecutionTrustProfileReference("trust-profile", "deleted", "7"),
            resolver,
            new RecordingCompiler(new TrustProfileProcessPolicyCompilation(false, null, [])));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.GetCompilationAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.GetCompilationAsync());

        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task GetCompilationAsync_CompilerFailure_IsCachedAndNeverDowngraded()
    {
        var resolver = new RecordingResolver(
            _ => Task.FromResult<RemoteTrustProfileResolution?>(
                new(new TrustProfile(), "7")));
        var compiler = new ThrowingCompiler();
        var context = new AgentExecutionTrustContext(
            new AgentExecutionTrustProfileReference("trust-profile", "restricted", "7"),
            resolver,
            compiler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.GetCompilationAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.GetCompilationAsync());

        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, compiler.CallCount);
    }

    private sealed class RecordingResolver(
        Func<CancellationToken, Task<RemoteTrustProfileResolution?>> resolve)
        : IRemoteTrustProfileResolver
    {
        public int CallCount { get; private set; }

        public Task<RemoteTrustProfileResolution?> ResolveAsync(
            string profileReference,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return resolve(cancellationToken);
        }
    }

    private sealed class RecordingCompiler(TrustProfileProcessPolicyCompilation result)
        : ITrustProfileProcessPolicyCompiler
    {
        public int CallCount { get; private set; }

        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
        {
            CallCount++;
            return result;
        }
    }

    private sealed class ThrowingCompiler : ITrustProfileProcessPolicyCompiler
    {
        public int CallCount { get; private set; }

        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
        {
            CallCount++;
            throw new InvalidOperationException("sensitive compiler diagnostic");
        }
    }
}
