using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Mxc.Sdk;
using Phantom.Workspaces.Llm.Core.Tests.Secrets;
using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ProcessHandleTests
{
    private static readonly TimeSpan RealProcessFailsafe = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ProcessHandle_UntakenOutputWithFakeBackend_StillDrains()
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
        Assert.False(result.TimedOut);
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

        Assert.False(result.TimedOut);
        Assert.Equal(bytes, captured.ToArray());
    }

    [Fact]
    public async Task ProcessHandle_EmptyBufferRead_ReturnsZero()
    {
        var backend = new FakeProcessBackend();
        await using var handle = new StreamingProcessHandle(backend);
        var output = handle.StandardOutput;

        var read = await output.ReadAsync(Array.Empty<byte>());

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task ProcessHandle_PumpError_PropagatesToObservedReader()
    {
        var failingSource = new ThrowingReadStream(new IOException("simulated pipe failure"));
        var backend = new FakeProcessBackend { StandardOutput = failingSource };
        await using var handle = new StreamingProcessHandle(backend);
        var output = handle.StandardOutput;

        await handle.WaitAsync();

        // Once the pump has completed with an error, an observed reader must see the error rather than
        // a silent zero-length read.
        var buffer = new byte[16];
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await output.ReadExactlyAsync(buffer));
        Assert.Contains("simulated pipe failure", ex.ToString());
    }

    [Fact]
    public async Task ProcessHandle_DisposeIdempotent_NoDoubleKill()
    {
        var backend = new FakeProcessBackend { HasExited = false };
        var handle = new StreamingProcessHandle(backend);

        await handle.DisposeAsync();
        Assert.Equal(1, backend.KillCount);

        // A second dispose is a no-op — never kills again, never throws.
        await handle.DisposeAsync();
        Assert.Equal(1, backend.KillCount);
    }

    [Fact]
    public async Task ProcessHandle_DisposeAfterExit_DoesNotKill()
    {
        var backend = new FakeProcessBackend { HasExited = true };
        var handle = new StreamingProcessHandle(backend);

        await handle.DisposeAsync();

        Assert.Equal(0, backend.KillCount);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task ProcessHandle_Kill_DelegatesToBackend()
    {
        var backend = new FakeProcessBackend { HasExited = false };
        var handle = new StreamingProcessHandle(backend);
        try
        {
            handle.Kill();
            Assert.Equal(1, backend.KillCount);
        }
        finally
        {
            await handle.DisposeAsync();
        }
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

        Assert.Equal(0, backend.KillCount);
    }

    [Fact]
    public async Task ProcessHandle_MxcTimeoutAndMetadata_ArePreserved()
    {
        var metadata = new SandboxOutputMetadata();
        var backend = new FakeProcessBackend
        {
            WaitResult = ProcessExitResult.Create(-1, true, metadata),
        };
        await using var handle = new StreamingProcessHandle(backend);

        var result = await handle.WaitAsync();

        Assert.True(result.TimedOut);
        Assert.Same(metadata, result.OutputMetadata);
    }

    // ── Real-pipe integration coverage (Windows-only) ────────────────────────────

    [WindowsFact]
    public async Task ProcessHandle_UntakenOutput_DoesNotDeadlock()
    {
        // powershell child emits ~800 KiB on stdout AND stderr simultaneously without any consumer
        // taking the streams. If the OS pipe buffers filled and remained undrained, the child
        // would block indefinitely. The eager pump must keep the OS pipes drained so the child exits.
        var executor = new ProcessExecutor();
        await using var handle = executor.Start(new ProcessExecutionRequest(
            "powershell.exe",
            [
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "1..10000 | ForEach-Object { Write-Output ('X' * 80); [Console]::Error.WriteLine('Y' * 80) }",
            ]));

        using var failsafe = new CancellationTokenSource(RealProcessFailsafe);
        var result = await handle.WaitAsync(failsafe.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [WindowsFact]
    public async Task ProcessHandle_DisposeWhileRunning_KillsTree()
    {
        // Direct child (powershell) starts a grandchild (another powershell) via Start-Process,
        // prints the grandchild PID, then sleeps. After DisposeAsync both must be terminated.
        var executor = new ProcessExecutor();
        var handle = executor.Start(new ProcessExecutionRequest(
            "powershell.exe",
            [
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "$p = Start-Process -FilePath 'powershell.exe' -PassThru -NoNewWindow -ArgumentList "
                    + "'-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60'; "
                    + "Write-Output $p.Id; Start-Sleep -Seconds 60",
            ]));

        int grandchildPid;
        int directChildPid = handle.LaunchInfo.ProcessId ?? throw new InvalidOperationException("no pid");
        try
        {
            using var readCts = new CancellationTokenSource(RealProcessFailsafe);
            var reader = new StreamReader(handle.StandardOutput);
            var line = await reader.ReadLineAsync(readCts.Token)
                ?? throw new InvalidOperationException("grandchild PID not emitted");
            grandchildPid = int.Parse(line.Trim());
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }

        await handle.DisposeAsync();

        await WaitUntilProcessGone(directChildPid, RealProcessFailsafe);
        await WaitUntilProcessGone(grandchildPid, RealProcessFailsafe);
    }

    [WindowsFact]
    public async Task ProcessHandle_SystemProcess_Timeout_ReturnsTimedOutAndKills()
    {
        var executor = new ProcessExecutor();
        await using var handle = executor.Start(new ProcessExecutionRequest(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"])
        {
            Timeout = TimeSpan.FromSeconds(1),
        });

        using var failsafe = new CancellationTokenSource(RealProcessFailsafe);
        var result = await handle.WaitAsync(failsafe.Token);

        Assert.True(result.TimedOut);
    }

    [WindowsFact]
    public async Task ProcessHandle_SystemProcess_CallerCancellation_PropagatesOperationCancelled()
    {
        var executor = new ProcessExecutor();
        await using var handle = executor.Start(new ProcessExecutionRequest(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"])
        {
            Timeout = TimeSpan.FromSeconds(30),
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await handle.WaitAsync(cancellation.Token));
    }

    private static async Task WaitUntilProcessGone(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Yield();
        }
        throw new Xunit.Sdk.XunitException($"Process {pid} still running after {timeout}.");
    }
}

internal sealed class ThrowingReadStream : Stream
{
    private readonly Exception error;
    public ThrowingReadStream(Exception error) => this.error = error;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw error;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw error;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class FakeProcessBackend : IProcessBackend
{
    public int? ProcessId => 42;
    public bool IsContained { get; init; }
    public Stream StandardInput { get; init; } = new MemoryStream();
    public Stream StandardOutput { get; init; } = new MemoryStream();
    public Stream StandardError { get; init; } = new MemoryStream();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public ProcessPathCategory PathCategory { get; init; } = ProcessPathCategory.CallerProvided;
    public bool HasExited { get; set; } = true;
    public int KillCount { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? KillException { get; init; }
    public ProcessExitResult WaitResult { get; init; } = ProcessExitResult.Create(0, false, null);

    public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<ProcessExitResult>(cancellationToken)
            : Task.FromResult(WaitResult);

    public void Kill()
    {
        if (KillException is not null)
            throw KillException;
        KillCount++;
        HasExited = true;
    }

    public void Dispose() => Disposed = true;
}
