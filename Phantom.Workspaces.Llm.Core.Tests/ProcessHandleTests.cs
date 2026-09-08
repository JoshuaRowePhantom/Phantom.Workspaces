using Microsoft.Mxc.Sdk;
using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ProcessHandleTests
{
    [Fact]
    public async Task ProcessHandle_UntakenOutput_DoesNotDeadlock()
    {
        var bytes = new byte[1024 * 1024];
        var backend = new FakeProcessBackend
        {
            StandardOutput = new MemoryStream(bytes),
            StandardError = new MemoryStream(bytes),
        };
        await using var handle = new StreamingProcessHandle(backend);

        var result = await handle.WaitAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task ProcessHandle_ConsumedOutput_PreservesBytes()
    {
        var bytes = Enumerable.Range(0, 1024 * 1024)
            .Select(index => (byte)index)
            .ToArray();
        var backend = new FakeProcessBackend { StandardOutput = new MemoryStream(bytes) };
        await using var handle = new StreamingProcessHandle(backend);
        var output = handle.StandardOutput;

        var waitTask = handle.WaitAsync();
        using var captured = new MemoryStream();
        await output.CopyToAsync(captured);
        var result = await waitTask;

        Assert.False(result.StandardOutputTruncated);
        Assert.Equal(bytes, captured.ToArray());
    }

    [Fact]
    public async Task ProcessHandle_DisposeWhileRunning_KillsTree()
    {
        var backend = new FakeProcessBackend { HasExited = false };
        var handle = new StreamingProcessHandle(backend);

        await handle.DisposeAsync();

        Assert.True(backend.Killed);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task ProcessHandle_KillFailure_StillDisposesResources()
    {
        var backend = new FakeProcessBackend
        {
            HasExited = false,
            KillException = new InvalidOperationException("kill failed"),
        };
        var handle = new StreamingProcessHandle(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await handle.DisposeAsync());

        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task ProcessHandle_WaitCancelled_DoesNotReportSuccessfulExit()
    {
        var backend = new FakeProcessBackend();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var handle = new StreamingProcessHandle(backend);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.WaitAsync(cancellation.Token));

        Assert.False(backend.Killed);
    }

    [Fact]
    public async Task ProcessHandle_MxcTimeoutAndMetadata_ArePreserved()
    {
        var metadata = new SandboxOutputMetadata();
        var backend = new FakeProcessBackend
        {
            WaitResult = new ProcessExitResult(-1, true, metadata),
        };
        await using var handle = new StreamingProcessHandle(backend);

        var result = await handle.WaitAsync();

        Assert.True(result.TimedOut);
        Assert.Same(metadata, result.OutputMetadata);
    }
}

internal sealed class FakeProcessBackend : IProcessBackend
{
    public int? ProcessId => 42;
    public bool IsContained { get; init; }
    public Stream StandardInput { get; init; } = new MemoryStream();
    public Stream StandardOutput { get; init; } = new MemoryStream();
    public Stream StandardError { get; init; } = new MemoryStream();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasExited { get; set; } = true;
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? KillException { get; init; }
    public ProcessExitResult WaitResult { get; init; } = new(0, false, null);

    public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<ProcessExitResult>(cancellationToken)
            : Task.FromResult(WaitResult);

    public void Kill()
    {
        if (KillException is not null)
            throw KillException;
        Killed = true;
        HasExited = true;
    }

    public void Dispose() => Disposed = true;
}
