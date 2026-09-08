using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers <see cref="ProcessExecutorBackedClientTransport"/> and <c>ProcessOwnedMcpTransport</c>
/// (issue #1477): the transport must be single-connect, must launch through the injected
/// <see cref="IProcessExecutor"/> (which itself owns the null / MXC policy switch), must drain
/// stderr separately from the JSON-RPC stdout channel, must dispose its process on teardown, and
/// must not retry with a null policy when a required containment launch fails.
/// </summary>
public sealed class ProcessExecutorBackedClientTransportTests
{
    [Fact]
    public async Task ConnectAsync_SecondCall_ThrowsInvalidOperation()
    {
        var executor = new StubProcessExecutor();
        var transport = new ProcessExecutorBackedClientTransport(
            "test",
            new ProcessExecutionRequest("some.exe", []),
            executor,
            NullLoggerFactory.Instance);

        _ = transport.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_StderrOutput_DoesNotEnterProtocolStream()
    {
        // Stderr must be drained by the transport's own reader and never appear on the JSON-RPC
        // MessageReader channel — otherwise a chatty child would poison the protocol stream.
        var executor = new StubProcessExecutor();
        var stderrPayload = System.Text.Encoding.UTF8.GetBytes("server debug: starting up\n");
        executor.SeedStderr(stderrPayload);

        var transport = new ProcessExecutorBackedClientTransport(
            "test",
            new ProcessExecutionRequest("some.exe", []),
            executor,
            NullLoggerFactory.Instance);
        var inner = await transport.ConnectAsync();

        // Give the drainer a beat to consume the seeded bytes.
        await Task.Yield();
        await Task.Delay(50);

        Assert.False(inner.MessageReader.TryRead(out _),
            "Stderr bytes must never surface on the JSON-RPC MessageReader.");
        await inner.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_RunningServer_KillsProcessAndDrainsTasks()
    {
        var executor = new StubProcessExecutor();

        var transport = new ProcessExecutorBackedClientTransport(
            "test",
            new ProcessExecutionRequest("some.exe", []),
            executor,
            NullLoggerFactory.Instance);
        var inner = await transport.ConnectAsync();

        await inner.DisposeAsync();

        Assert.True(executor.LastHandle!.Disposed, "Underlying IProcessHandle must be disposed by the transport.");
    }

    [Fact]
    public async Task ConnectAsync_MxcLaunchFails_DoesNotRetryWithoutPolicy()
    {
        // A required-containment launch that fails must NOT be silently retried without the policy.
        // The transport instance is one-shot; once ConnectAsync throws, a second call must fail too
        // and no second executor.Start is performed with a null policy.
        var executor = new StubProcessExecutor { StartException = new InvalidOperationException("mxc denied") };
        var policy = new MxcProcessPolicy(
            MxcProcessPolicy.CurrentSchemaVersion,
            [], [], [],
            new Dictionary<string, string>(),
            new MxcProcessContainment(
                MxcContainmentBackend.ProcessContainer,
                LeastPrivilege: true,
                LearningMode: false,
                PermissiveMode: false));

        var transport = new ProcessExecutorBackedClientTransport(
            "test",
            new ProcessExecutionRequest("some.exe", []) { MxcPolicy = policy },
            executor,
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.ConnectAsync());
        // Second attempt is refused before touching the executor again.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.ConnectAsync());
        Assert.Equal(1, executor.StartCount);
    }

    // Stub IProcessExecutor whose IProcessHandle exposes disconnected pipes so the SDK's
    // StreamClientTransport has stdin/stdout to connect to without needing a real child process.
    private sealed class StubProcessExecutor : IProcessExecutor
    {
        public Exception? StartException { get; set; }
        public int StartCount { get; private set; }
        public StubProcessHandle? LastHandle { get; private set; }

        public void SeedStderr(byte[] bytes) => pendingStderr = bytes;

        private byte[]? pendingStderr;

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            StartCount++;
            if (StartException is not null)
                throw StartException;

            LastHandle = new StubProcessHandle(pendingStderr);
            pendingStderr = null;
            return LastHandle;
        }
    }

    private sealed class StubProcessHandle : IProcessHandle
    {
        private readonly MemoryStream stdin = new();
        private readonly PipeStream stdout = new();
        private readonly PipeStream stderr = new();
        private readonly TaskCompletionSource<ProcessExitResult> exit = new();

        public StubProcessHandle(byte[]? seededStderr)
        {
            if (seededStderr is not null)
                stderr.WriteInitial(seededStderr);
            LaunchInfo = new ProcessLaunchInfo(1234, false, []);
        }

        public bool Disposed { get; private set; }
        public Stream StandardInput => stdin;
        public Stream StandardOutput => stdout;
        public Stream StandardError => stderr;
        public ProcessLaunchInfo LaunchInfo { get; }
        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default) => exit.Task;
        public void Kill() => exit.TrySetResult(new ProcessExitResult(-1, false, null));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            stderr.Complete();
            stdout.Complete();
            exit.TrySetResult(new ProcessExitResult(0, false, null));
            return ValueTask.CompletedTask;
        }
    }

    // Simple pipe-like stream that supports async reads which block until bytes are written
    // (or the writer completes) so the transport's stream readers behave like real OS pipes.
    private sealed class PipeStream : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int currentOffset;

        public void WriteInitial(byte[] bytes) => chunks.Writer.TryWrite(bytes);
        public void Complete() => chunks.Writer.TryComplete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (current is null || currentOffset == current.Length)
            {
                if (!await chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                if (!chunks.Reader.TryRead(out current))
                    continue;
                currentOffset = 0;
            }

            var n = Math.Min(buffer.Length, current.Length - currentOffset);
            current.AsMemory(currentOffset, n).CopyTo(buffer);
            currentOffset += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
