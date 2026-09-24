using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Services.Logging;

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
    public async Task ProcessExecutorBackedClientTransport_StderrContainsSecret_DoesNotLogOrThrowSecret()
    {
        const string secret = "private-stderr-token-and-prompt";
        var directory = Path.Combine(AppContext.BaseDirectory, "mcp-trace-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            var executor = new StubProcessExecutor();
            var transport = new ProcessExecutorBackedClientTransport(
                "private-server-name", new ProcessExecutionRequest("some.exe"), executor, process);
            var inner = await transport.ConnectAsync();
            executor.LastHandle!.Exit(23, secret);

            var failure = await Assert.ThrowsAsync<IOException>(
                async () => await inner.MessageReader.Completion);
            Assert.Contains("code 23", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-server-name", failure.Message, StringComparison.Ordinal);
            await inner.DisposeAsync();
            var path = Assert.Single(Directory.GetFiles(directory, "phantom-workspaces-*.log"));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            Assert.Contains("stderr drained", content, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
            Assert.DoesNotContain("private-server-name", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Name_ConstructedTransport_ReturnsConfiguredName()
    {
        var transport = new ProcessExecutorBackedClientTransport(
            "restricted-tool",
            new ProcessExecutionRequest("some.exe"),
            new StubProcessExecutor(),
            NullLoggerFactory.Instance);

        Assert.Equal("restricted-tool", transport.Name);
    }

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

        await executor.LastHandle!.StderrRead;

        Assert.False(inner.MessageReader.TryRead(out _),
            "Stderr bytes must never surface on the JSON-RPC MessageReader.");
        await inner.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_Streams_McpHandshakeCompletes()
    {
        var executablePath = TestMcpServerProcess.GetMcpExecutablePath();
        var transport = new ProcessExecutorBackedClientTransport(
            "test-mcp-stdio",
            new ProcessExecutionRequest(executablePath, ["--mode", "stdio"])
            {
                WorkingDirectory = Path.GetDirectoryName(executablePath),
            },
            new ProcessExecutor(),
            NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => string.Equals(tool.Name, "ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PrematureNonzeroExit_FaultsMessageReader_WithBoundedSanitizedStderr()
    {
        var executor = new StubProcessExecutor();
        var transport = new ProcessExecutorBackedClientTransport(
            "test",
            new ProcessExecutionRequest("some.exe", []),
            executor,
            NullLoggerFactory.Instance);
        var inner = await transport.ConnectAsync();
        var oversizedDiagnostic = "\u001b" + new string('x', 9 * 1024);

        executor.LastHandle!.Exit(23, oversizedDiagnostic);

        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await inner.MessageReader.Completion);
        Assert.Contains("code 23", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', exception.Message);
        Assert.True(exception.Message.Length < 9 * 1024);
        await inner.DisposeAsync();
    }

    [Fact]
    public async Task PrematureNonzeroExit_DrainsFinalProtocolMessageBeforeFaulting()
    {
        var boundaryReader = new BoundaryChannelReader();
        var inner = new StubTransport(boundaryReader);
        var handle = new StubProcessHandle(seededStderr: null);
        using var drainCts = new CancellationTokenSource();
        var drainer = new ProcessExecutorBackedClientTransport.StderrDrainer(
            handle.StandardError,
            NullLogger.Instance,
            "test",
            drainCts.Token);
        var transport = new ProcessOwnedMcpTransport(
            inner,
            handle,
            drainer,
            drainCts,
            handle.WaitAsync(),
            "test");
        var finalMessage = new JsonRpcNotification { Method = "final" };

        await boundaryReader.FirstWait.Task;
        handle.Exit(7, "failure");
        await boundaryReader.ExitDrainWait.Task;
        boundaryReader.PublishAndComplete(finalMessage);

        Assert.Same(finalMessage, await transport.MessageReader.ReadAsync());
        await Assert.ThrowsAsync<IOException>(async () => await transport.MessageReader.Completion);
        await transport.DisposeAsync();
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

    [Fact]
    public async Task ConnectAsync_DisposedDuringStart_CancelsAndJoinsStartup()
    {
        var executor = new CancelAwareProcessExecutor();
        var transport = new ProcessExecutorBackedClientTransport(
            "test",
            new ProcessExecutionRequest("some.exe"),
            executor,
            NullLoggerFactory.Instance);

        var connectTask = transport.ConnectAsync();
        await executor.Started.Task;

        var disposeTask = transport.DisposeAsync().AsTask();

        await executor.CancellationObserved.Task;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
        await disposeTask;
        Assert.True(disposeTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SeparateTransports_OwnSeparateProcesses_AndDisposeEachOnce()
    {
        var executor = new StubProcessExecutor();
        var first = new ProcessExecutorBackedClientTransport(
            "first",
            new ProcessExecutionRequest("first.exe"),
            executor,
            NullLoggerFactory.Instance);
        var second = new ProcessExecutorBackedClientTransport(
            "second",
            new ProcessExecutionRequest("second.exe"),
            executor,
            NullLoggerFactory.Instance);

        await first.ConnectAsync();
        await second.ConnectAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();

        Assert.Equal(2, executor.Handles.Count);
        Assert.All(executor.Handles, handle => Assert.Equal(1, handle.DisposeCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendMessageAsync_TransportFailure_DisposesOwnedProcessBeforePropagating(
        bool throwSynchronously)
    {
        var sendFailure = new IOException("stdio write failed");
        var inner = new StubTransport
        {
            SendException = sendFailure,
            ThrowSynchronously = throwSynchronously,
        };
        var handle = new StubProcessHandle(seededStderr: null);
        using var drainCts = new CancellationTokenSource();
        var drainer = new ProcessExecutorBackedClientTransport.StderrDrainer(
            handle.StandardError,
            NullLogger.Instance,
            "test",
            drainCts.Token);
        var transport = new ProcessOwnedMcpTransport(
            inner,
            handle,
            drainer,
            drainCts,
            handle.WaitAsync(),
            "test");

        var exception = await Assert.ThrowsAsync<IOException>(
            () => transport.SendMessageAsync(new JsonRpcNotification { Method = "test" }));

        Assert.Same(sendFailure, exception);
        Assert.Equal(1, handle.DisposeCount);
        await transport.DisposeAsync();
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task StdoutEof_DisposesOwnedProcessAndCompletesReader()
    {
        var inner = new StubTransport();
        var handle = new StubProcessHandle(seededStderr: null);
        using var drainCts = new CancellationTokenSource();
        var drainer = new ProcessExecutorBackedClientTransport.StderrDrainer(
            handle.StandardError,
            NullLogger.Instance,
            "test",
            drainCts.Token);
        var transport = new ProcessOwnedMcpTransport(
            inner,
            handle,
            drainer,
            drainCts,
            handle.WaitAsync(),
            "test");
        inner.Complete();
        inner.Complete();

        await transport.MessageReader.Completion;
        await handle.DisposedTask;
        Assert.Equal(1, handle.DisposeCount);
        await transport.DisposeAsync();
        Assert.Equal(1, handle.DisposeCount);
    }

    [Fact]
    public async Task ProcessExit_DrainsFinalFramePublishedAtReaderBoundary()
    {
        var boundaryReader = new BoundaryChannelReader();
        var inner = new StubTransport(boundaryReader);
        var handle = new StubProcessHandle(seededStderr: null);
        using var drainCts = new CancellationTokenSource();
        var drainer = new ProcessExecutorBackedClientTransport.StderrDrainer(
            handle.StandardError,
            NullLogger.Instance,
            "test",
            drainCts.Token);
        var transport = new ProcessOwnedMcpTransport(
            inner,
            handle,
            drainer,
            drainCts,
            handle.WaitAsync(),
            "test");
        var finalMessage = new JsonRpcNotification { Method = "final-boundary" };

        await boundaryReader.FirstWait.Task;
        handle.Exit(0, string.Empty);
        await boundaryReader.ExitDrainWait.Task;
        boundaryReader.PublishAndComplete(finalMessage);

        Assert.Same(finalMessage, await transport.MessageReader.ReadAsync());
        await transport.MessageReader.Completion;
        Assert.Equal(1, handle.DisposeCount);
        await transport.DisposeAsync();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task DisposeAsync_CleanupFailures_PreservesFirstAndObservesAll(
        bool innerFails,
        bool handleFails,
        bool drainerFails)
    {
        var innerFailure = new InvalidOperationException("inner cleanup");
        var handleFailure = new IOException("handle cleanup");
        var drainerFailure = new NotSupportedException("drainer cleanup");
        var inner = new StubTransport
        {
            DisposeException = innerFails ? innerFailure : null,
        };
        var handle = new StubProcessHandle(
            seededStderr: null,
            disposeException: handleFails ? handleFailure : null,
            standardError: drainerFails ? new FaultingReadStream(drainerFailure) : null);
        using var drainCts = new CancellationTokenSource();
        var loggerFactory = new CapturingLoggerFactory();
        var drainer = new ProcessExecutorBackedClientTransport.StderrDrainer(
            handle.StandardError,
            loggerFactory.CreateLogger("test"),
            "test",
            drainCts.Token);
        var transport = new ProcessOwnedMcpTransport(
            inner,
            handle,
            drainer,
            drainCts,
            handle.WaitAsync(),
            "test",
            loggerFactory.CreateLogger("test"));
        if (drainerFails)
        {
            await Task.WhenAny(drainer.PumpTask);
            Assert.True(drainer.PumpTask.IsFaulted);
        }

        var thrown = await Record.ExceptionAsync(
            () => transport.DisposeAsync().AsTask());

        Assert.Same(
            innerFails ? innerFailure : handleFails ? handleFailure : drainerFailure,
            thrown);
        Assert.Equal(1, handle.DisposeCount);
        var failureCount = (innerFails ? 1 : 0) + (handleFails ? 1 : 0) + (drainerFails ? 1 : 0);
        if (failureCount == 1)
            Assert.Contains(loggerFactory.Entries, entry =>
                entry.Message.Contains("category failure", StringComparison.Ordinal));
        Assert.DoesNotContain(loggerFactory.Entries, entry =>
            entry.Message.Contains("inner cleanup", StringComparison.Ordinal)
            || entry.Message.Contains("handle cleanup", StringComparison.Ordinal)
            || entry.Message.Contains("drainer cleanup", StringComparison.Ordinal));
        if (failureCount > 1)
            Assert.True(loggerFactory.Entries.Count >= 2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConnectAsync_ProtocolFailure_IsNotReplacedByCleanupFailures(
        bool handleFails,
        bool drainerFails)
    {
        var protocolFailure = new InvalidDataException("protocol startup");
        var handleFailure = new IOException("handle cleanup");
        var drainerFailure = new NotSupportedException("drainer cleanup");
        var protocolEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectProtocol = new TaskCompletionSource<ITransport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new CoordinatedReadStream(
            drainerFails ? drainerFailure : null);
        var handle = new StubProcessHandle(
            seededStderr: null,
            disposeException: handleFails ? handleFailure : null,
            standardError: stderr,
            coordinateDisposal: true);
        var executor = new StubProcessExecutor
        {
            NextHandle = handle,
        };
        var loggerFactory = new CapturingLoggerFactory();
        var transport = new ProcessExecutorBackedClientTransport(
            "test",
            new ProcessExecutionRequest("some.exe"),
            executor,
            loggerFactory,
            (_, _, _, _) =>
            {
                protocolEntered.TrySetResult();
                return rejectProtocol.Task;
            });

        var connection = transport.ConnectAsync();
        await protocolEntered.Task;
        await stderr.ReadEntered;
        rejectProtocol.TrySetException(protocolFailure);

        await handle.DisposeEntered;
        Assert.False(connection.IsCompleted);
        handle.AllowDispose();
        await handle.DisposeCompleted;

        Assert.False(connection.IsCompleted);
        stderr.CompleteRead();
        await stderr.ReadCompleted;

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => connection);

        Assert.Same(protocolFailure, thrown);
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(
            (handleFails ? 1 : 0) + (drainerFails ? 1 : 0),
            loggerFactory.Entries.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    // Stub IProcessExecutor whose IProcessHandle exposes disconnected pipes so the SDK's
    // StreamClientTransport has stdin/stdout to connect to without needing a real child process.
    private sealed class StubProcessExecutor : IProcessExecutor
    {
        public Exception? StartException { get; set; }
        public int StartCount { get; private set; }
        public StubProcessHandle? LastHandle { get; private set; }
        public List<StubProcessHandle> Handles { get; } = [];
        public StubProcessHandle? NextHandle { get; init; }

        public void SeedStderr(byte[] bytes) => pendingStderr = bytes;

        private byte[]? pendingStderr;

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            StartCount++;
            if (StartException is not null)
                throw StartException;

            LastHandle = NextHandle ?? new StubProcessHandle(pendingStderr);
            Handles.Add(LastHandle);
            pendingStderr = null;
            return LastHandle;
        }
    }

    private sealed class CancelAwareProcessExecutor : IProcessExecutor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IProcessHandle Start(ProcessExecutionRequest request) =>
            throw new NotSupportedException();

        public async Task<IProcessHandle> StartAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            var neverCompletes = new TaskCompletionSource<IProcessHandle>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                () =>
                {
                    CancellationObserved.TrySetResult();
                    neverCompletes.TrySetCanceled(cancellationToken);
                });
            return await neverCompletes.Task;
        }
    }

    private sealed class StubProcessHandle : IProcessHandle
    {
        private readonly MemoryStream stdin = new();
        private readonly PipeStream stdout = new();
        private readonly PipeStream stderr = new();
        private readonly TaskCompletionSource<ProcessExitResult> exit = new();
        private readonly TaskCompletionSource stderrRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowDispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Exception? disposeException;
        private int disposeCount;

        public StubProcessHandle(
            byte[]? seededStderr,
            Exception? disposeException = null,
            Stream? standardError = null,
            bool coordinateDisposal = false)
        {
            this.disposeException = disposeException;
            StandardError = standardError ?? stderr;
            if (!coordinateDisposal)
                allowDispose.TrySetResult();
            if (ReferenceEquals(StandardError, stderr))
            {
                stderr.ReadObserved = () => stderrRead.TrySetResult();
                if (seededStderr is not null)
                    stderr.WriteInitial(seededStderr);
            }
            LaunchInfo = new ProcessLaunchInfo
            {
                ProcessId = 1234,
                IsContained = false,
                Warnings = [],
                PathCategory = ProcessPathCategory.CallerProvided,
                LaunchMechanism = ProcessLaunchMechanism.OrdinaryProcess,
                CreationStatusAvailable = true,
                CreateProcessSucceeded = true,
                CreateProcessWin32Error = null,
                SdkSpawnSucceeded = null,
                JobConfigured = null,
                JobAssigned = null,
                ResumeSucceeded = null,
            };
        }

        public bool Disposed { get; private set; }
        public int DisposeCount => Volatile.Read(ref disposeCount);
        public Task DisposedTask => disposed.Task;
        public Task DisposeEntered => disposeEntered.Task;
        public Task DisposeCompleted => disposeCompleted.Task;
        public Task StderrRead => stderrRead.Task;
        public Stream StandardInput => stdin;
        public Stream StandardOutput => stdout;
        public Stream StandardError { get; }
        public ProcessLaunchInfo LaunchInfo { get; }
        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default) => exit.Task;
        public void Kill()
        {
            stderr.Complete();
            stdout.Complete();
            exit.TrySetResult(ProcessExitResult.Create(-1, false, null));
        }

        public void Exit(int exitCode, string stderrText)
        {
            stderr.WriteInitial(System.Text.Encoding.UTF8.GetBytes(stderrText + "\n"));
            stderr.Complete();
            stdout.Complete();
            exit.TrySetResult(ProcessExitResult.Create(exitCode, false, null));
        }

        public void AllowDispose() => allowDispose.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposeCount, 1) != 0)
                return;

            disposeEntered.TrySetResult();
            await allowDispose.Task;
            try
            {
                Disposed = true;
                stderr.Complete();
                stdout.Complete();
                exit.TrySetResult(ProcessExitResult.Create(0, false, null));
                disposed.TrySetResult();
                if (disposeException is not null)
                    throw disposeException;
            }
            finally
            {
                disposeCompleted.TrySetResult();
            }
        }
    }

    private sealed class StubTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> messages = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly ChannelReader<JsonRpcMessage>? reader;

        public StubTransport()
        {
        }

        public StubTransport(ChannelReader<JsonRpcMessage> reader)
        {
            this.reader = reader;
        }

        public Exception? SendException { get; init; }
        public Exception? DisposeException { get; init; }
        public bool ThrowSynchronously { get; init; }
        public string? SessionId => null;
        public ChannelReader<JsonRpcMessage> MessageReader => reader ?? messages.Reader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (ThrowSynchronously && SendException is not null)
                throw SendException;
            return SendException is null ? Task.CompletedTask : Task.FromException(SendException);
        }
        public void WriteAndComplete(JsonRpcMessage message)
        {
            messages.Writer.TryWrite(message);
            messages.Writer.TryComplete();
        }
        public void Complete() => messages.Writer.TryComplete();
        public ValueTask DisposeAsync()
        {
            messages.Writer.TryComplete();
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class BoundaryChannelReader : ChannelReader<JsonRpcMessage>
    {
        private readonly Channel<JsonRpcMessage> channel = Channel.CreateUnbounded<JsonRpcMessage>();
        private int waitCount;

        public TaskCompletionSource FirstWait { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExitDrainWait { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PublishAndComplete(JsonRpcMessage message)
        {
            this.channel.Writer.TryWrite(message);
            this.channel.Writer.TryComplete();
        }

        public override bool TryRead(out JsonRpcMessage item) =>
            this.channel.Reader.TryRead(out item!);

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref this.waitCount);
            if (count == 1)
                this.FirstWait.TrySetResult();
            else if (count == 2)
                this.ExitDrainWait.TrySetResult();
            return this.channel.Reader.WaitToReadAsync(cancellationToken);
        }
    }

    private sealed class FaultingReadStream(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CoordinatedReadStream(Exception? failure) : Stream
    {
        private readonly TaskCompletionSource readEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completeRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource readCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadEntered => readEntered.Task;
        public Task ReadCompleted => readCompleted.Task;

        public void CompleteRead() => completeRead.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readEntered.TrySetResult();
            try
            {
                await completeRead.Task;
                if (failure is not null)
                    throw failure;
                return 0;
            }
            finally
            {
                readCompleted.TrySetResult();
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
            if (!chunks.Reader.TryPeek(out _) && currentOffset == current.Length)
                ReadObserved?.Invoke();
            return n;
        }
        public Action? ReadObserved { get; set; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
