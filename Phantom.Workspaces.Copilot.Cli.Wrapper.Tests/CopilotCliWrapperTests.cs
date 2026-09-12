using Phantom.Workspaces.Copilot.Cli.Wrapper;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Processes;
using System.Text;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Copilot.Cli.Wrapper.Tests;

[CollectionDefinition("Copilot wrapper MXC", DisableParallelization = true)]
public sealed class CopilotWrapperMxcCollection;

[Collection("Copilot wrapper MXC")]
public sealed class CopilotCliWrapperTests
{
    [Fact]
    public async Task Wrapper_InvalidArguments_ReturnsReservedExitCode()
    {
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", "missing-prefix"],
            Stream.Null,
            stdout,
            stderr,
            new RecordingExecutor(),
            "unused",
            Environment.ProcessId,
            "unused");

        Assert.Equal(CopilotCliWrapper.InvalidArgumentsExitCode, exitCode);
        Assert.Empty(stdout.ToArray());
        Assert.Equal(
            $"Invalid wrapper arguments.{Environment.NewLine}",
            Encoding.UTF8.GetString(stderr.ToArray()));
    }

    [Fact]
    public async Task Wrapper_InvalidPolicyEnvelope_ReturnsReservedExitCode()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        File.WriteAllText(lease.Path, """{"secret":"must-not-leak"}""");
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            Stream.Null,
            stdout,
            stderr,
            new RecordingExecutor(),
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(CopilotCliWrapper.InvalidEnvelopeExitCode, exitCode);
        Assert.Empty(stdout.ToArray());
        Assert.Equal(
            $"Invalid or expired Copilot policy.{Environment.NewLine}",
            Encoding.UTF8.GetString(stderr.ToArray()));
        Assert.False(File.Exists(lease.Path));
    }

    [Fact]
    public async Task Wrapper_StreamRelayFails_ReturnsInternalFailureExitCode()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        using var stderr = new MemoryStream();
        var executor = new RecordingExecutor { ChildOutput = [1] };

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            Stream.Null,
            new ThrowingWriteStream(),
            stderr,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(CopilotCliWrapper.InternalFailureExitCode, exitCode);
        Assert.Equal(
            $"Copilot wrapper stream relay failed.{Environment.NewLine}",
            Encoding.UTF8.GetString(stderr.ToArray()));
    }

    [Fact]
    public async Task Wrapper_CopilotPathEqualsWrapper_RejectsRecursion()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.WrapperPath],
            Stream.Null,
            Stream.Null,
            stderr,
            new RecordingExecutor(),
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(CopilotCliWrapper.UnsafePathExitCode, exitCode);
        Assert.Contains("unsafe", Encoding.UTF8.GetString(stderr.ToArray()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrapper_StdioTraffic_RelaysBytesWithoutProtocolContamination()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var executor = new RecordingExecutor
        {
            ChildOutput = [0, 1, 2, 255],
            ChildError = Encoding.UTF8.GetBytes("warning"),
            ExitCode = 23,
        };
        using var stdin = new MemoryStream([9, 8, 7]);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath, "--stdio"],
            stdin,
            stdout,
            stderr,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(23, exitCode);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, stdout.ToArray());
        Assert.Equal("warning", Encoding.UTF8.GetString(stderr.ToArray()));
        Assert.Equal(new byte[] { 9, 8, 7 }, executor.Handle!.WrittenInput);
    }

    [Fact]
    public async Task Wrapper_SdkArguments_ForwardsAllRemainingArgumentsUnchanged()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var executor = new RecordingExecutor();
        var forwarded = new[]
        {
            "--headless", "--no-auto-update", "--log-level", "debug", "--stdio",
            "--auth-token-env", "COPILOT_SDK_AUTH_TOKEN", "--no-auto-login",
            "--session-idle-timeout", "300", "--remote",
        };

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath, .. forwarded],
            Stream.Null,
            Stream.Null,
            Stream.Null,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(forwarded, executor.Request!.Arguments);
        Assert.Equal(Path.GetFullPath(files.CopilotPath), executor.Request.Executable);
        Assert.NotNull(executor.Request.MxcPolicy);
    }

    [Fact]
    public async Task Wrapper_MxcLaunchFails_DoesNotLaunchDirectCli()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var executor = new ThrowingExecutor();
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            Stream.Null,
            Stream.Null,
            stderr,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(CopilotCliWrapper.ExecutorFailureExitCode, exitCode);
        Assert.Equal(1, executor.StartCount);
        Assert.Contains("containment", Encoding.UTF8.GetString(stderr.ToArray()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrapper_ExpiredEnvelope_ReturnsInvalidEnvelopeExitCode()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var envelope = JsonNode.Parse(File.ReadAllText(lease.Path))!.AsObject();
        envelope["createdUtc"] = DateTimeOffset.UnixEpoch;
        envelope["expiresUtc"] = DateTimeOffset.UnixEpoch.AddMinutes(1);
        File.WriteAllText(lease.Path, envelope.ToJsonString());
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            Stream.Null,
            stdout,
            stderr,
            new RecordingExecutor(),
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(CopilotCliWrapper.InvalidEnvelopeExitCode, exitCode);
        Assert.Empty(stdout.ToArray());
        Assert.Contains("expired", Encoding.UTF8.GetString(stderr.ToArray()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrapper_IntermediateAncestorReparsePoint_ReturnsUnsafePathExitCode()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var target = Directory.CreateDirectory(Path.Combine(files.BaseDirectory, "real-cli"));
        var child = Directory.CreateDirectory(Path.Combine(target.FullName, "child"));
        File.WriteAllText(Path.Combine(child.FullName, "copilot.exe"), "cli");
        var link = Path.Combine(files.BaseDirectory, "linked-cli");
        await CreateJunctionAsync(link, target.FullName);
        var executor = new RecordingExecutor();

        try
        {
            var exitCode = await CopilotCliWrapper.RunAsync(
                [
                    "--policy", lease.Path,
                    "--copilot", Path.Combine(link, "child", "copilot.exe"),
                ],
                Stream.Null,
                Stream.Null,
                Stream.Null,
                executor,
                files.LaunchRoot,
                Environment.ProcessId,
                files.WrapperPath);

            Assert.Equal(CopilotCliWrapper.UnsafePathExitCode, exitCode);
            Assert.Null(executor.Request);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Wrapper_Cancellation_TerminatesContainedProcessTree()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        using var cancellation = new CancellationTokenSource();
        var executor = new BlockingExecutor();
        var run = CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            new NeverCompletingStream(),
            Stream.Null,
            Stream.Null,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath,
            cancellation.Token);
        await executor.Started.Task;
        cancellation.Cancel();

        Assert.Equal(CopilotCliWrapper.InternalFailureExitCode, await run);
        Assert.True(executor.Handle.Killed);
    }

    [Fact]
    public async Task Wrapper_StdinEof_TerminatesContainedProcessTree()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var executor = new BlockingExecutor();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            Stream.Null,
            Stream.Null,
            Stream.Null,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(137, exitCode);
        Assert.True(executor.Handle.Killed);
    }

    [Fact]
    public async Task Wrapper_StdioPumps_RunConcurrentlyWhileInputAndChildRemainOpen()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        using var cancellation = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = new BlockingExecutor([0, 255, 1], [2, 254, 3]);
        using var stdout = new SignalingWriteStream();
        using var stderr = new SignalingWriteStream();
        var run = CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            new NeverCompletingStream(),
            stdout,
            stderr,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath,
            cancellation.Token);

        await executor.Started.Task.WaitAsync(timeout.Token);
        await Task.WhenAll(stdout.Written.Task, stderr.Written.Task).WaitAsync(timeout.Token);

        Assert.False(run.IsCompleted);
        Assert.Equal(new byte[] { 0, 255, 1 }, stdout.ToArray());
        Assert.Equal(new byte[] { 2, 254, 3 }, stderr.ToArray());

        cancellation.Cancel();
        Assert.Equal(CopilotCliWrapper.InternalFailureExitCode, await run);
        Assert.True(executor.Handle.Killed);
    }

    [Fact]
    public async Task Wrapper_ParentTerminates_KillsContainedProcessTree()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
            return;

        var wrapperPath = Path.Combine(
            AppContext.BaseDirectory,
            "phantom-copilot-wrapper.exe");
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(wrapperPath), $"Wrapper executable is missing: {wrapperPath}");
        Assert.True(File.Exists(powershellPath), $"Windows PowerShell is missing: {powershellPath}");

        using var handshakeDirectory = new WrapperFiles.TestDirectory("copilot-wrapper-handshake");
        var temporaryHandshakePath = Path.Combine(handshakeDirectory.Path, "child.tmp");
        var handshakePath = Path.Combine(handshakeDirectory.Path, "child.ready");
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(handshakeDirectory.Path)
        {
            EnableRaisingEvents = true,
        };
        watcher.Renamed += (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.FullPath, handshakePath, StringComparison.OrdinalIgnoreCase))
                handshake.TrySetResult();
        };
        using var lease = new CopilotLaunchPolicyStore().Create(
            CreatePolicy(readwritePaths: [handshakeDirectory.Path]),
            Environment.ProcessId);
        var escapedTemporaryPath = temporaryHandshakePath.Replace("'", "''", StringComparison.Ordinal);
        var escapedHandshakePath = handshakePath.Replace("'", "''", StringComparison.Ordinal);
        var script = $"""
            $child = Start-Process -FilePath $PSHOME\powershell.exe -ArgumentList @(
                '-NoLogo',
                '-NoProfile',
                '-NonInteractive',
                '-Command',
                '[Threading.ManualResetEvent]::new($false).WaitOne()'
            ) -PassThru
            [IO.File]::WriteAllText('{escapedTemporaryPath}', $child.Id)
            [IO.File]::Move('{escapedTemporaryPath}', '{escapedHandshakePath}')
            [Threading.ManualResetEvent]::new($false).WaitOne()
            """;
        using var wrapper = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = wrapperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "--policy", lease.Path,
                    "--copilot", powershellPath,
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    script,
                },
            }) ?? throw new InvalidOperationException("Failed to start the Copilot wrapper.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stderrTask = wrapper.StandardError.ReadToEndAsync(CancellationToken.None);
        System.Diagnostics.Process? descendant = null;
        try
        {
            var wrapperExitTask = wrapper.WaitForExitAsync(CancellationToken.None);
            var readinessTask = handshake.Task.WaitAsync(timeout.Token);
            if (await Task.WhenAny(readinessTask, wrapperExitTask) == wrapperExitTask)
            {
                await wrapperExitTask;
                Assert.Fail(
                    $"Wrapper exited before child readiness with code {wrapper.ExitCode}: " +
                    await stderrTask);
            }
            await readinessTask;
            descendant = System.Diagnostics.Process.GetProcessById(int.Parse(
                await File.ReadAllTextAsync(handshakePath, timeout.Token),
                System.Globalization.CultureInfo.InvariantCulture));

            wrapper.Kill(entireProcessTree: false);
            await wrapper.WaitForExitAsync(timeout.Token);
            await descendant.WaitForExitAsync(timeout.Token);

            Assert.True(descendant.HasExited);
        }
        finally
        {
            if (!wrapper.HasExited)
            {
                wrapper.Kill(entireProcessTree: true);
                await wrapper.WaitForExitAsync(CancellationToken.None);
            }
            if (descendant is { HasExited: false })
            {
                descendant.Kill(entireProcessTree: true);
                await descendant.WaitForExitAsync(CancellationToken.None);
            }
            descendant?.Dispose();
        }
    }

    private static MxcProcessPolicy CreatePolicy(IReadOnlyList<string>? readwritePaths = null) =>
        new(
            MxcProcessPolicy.CurrentSchemaVersion,
            [],
            readwritePaths ?? [],
            [],
            new Dictionary<string, string>(),
            new MxcProcessContainment(
                MxcContainmentBackend.ProcessContainer,
                LeastPrivilege: true,
                LearningMode: false,
                PermissiveMode: false));

    private sealed class RecordingExecutor : IProcessExecutor
    {
        public byte[] ChildOutput { get; init; } = [];
        public byte[] ChildError { get; init; } = [];
        public int ExitCode { get; init; }
        public ProcessExecutionRequest? Request { get; private set; }
        public RecordingHandle? Handle { get; private set; }

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            Request = request;
            Handle = new RecordingHandle(ChildOutput, ChildError, ExitCode);
            return Handle;
        }
    }

    private sealed class ThrowingExecutor : IProcessExecutor
    {
        public int StartCount { get; private set; }

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            StartCount++;
            throw new InvalidOperationException("MXC failed");
        }
    }

    private sealed class BlockingExecutor
        (byte[]? output = null, byte[]? error = null) : IProcessExecutor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BlockingHandle Handle { get; } = new(output ?? [], error ?? []);

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            Started.TrySetResult();
            return Handle;
        }
    }

    private sealed class BlockingHandle(byte[] output, byte[] error) : IProcessHandle
    {
        private readonly TaskCompletionSource<ProcessExitResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput { get; } = new MemoryStream(output, writable: false);
        public Stream StandardError { get; } = new MemoryStream(error, writable: false);
        public ProcessLaunchInfo LaunchInfo { get; } = CreateLaunchInfo();
        public bool Killed { get; private set; }

        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default) =>
            completion.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            Killed = true;
            completion.TrySetResult(CreateExitResult(137));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WaitForCancellationAsync(cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            WaitForCancellationAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private static async ValueTask<int> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
        }) ?? throw new InvalidOperationException("Failed to start junction creation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Junction creation failed: {await output} {await error}");
    }

    private sealed class RecordingHandle(byte[] output, byte[] error, int exitCode) : IProcessHandle
    {
        private readonly MemoryStream input = new();
        public Stream StandardInput => input;
        public Stream StandardOutput { get; } = new MemoryStream(output, writable: false);
        public Stream StandardError { get; } = new MemoryStream(error, writable: false);
        public ProcessLaunchInfo LaunchInfo { get; } = CreateLaunchInfo();
        public byte[] WrittenInput => input.ToArray();
        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateExitResult(exitCode));
        public void Kill() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Sensitive stream details.");

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("Sensitive stream details.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Sensitive stream details."));
    }

    private sealed class SignalingWriteStream : MemoryStream
    {
        internal TaskCompletionSource Written { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Written.TrySetResult();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            Written.TrySetResult();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            Written.TrySetResult();
        }
    }

    private static ProcessLaunchInfo CreateLaunchInfo() => new()
    {
        ProcessId = 123,
        IsContained = true,
        Warnings = [],
        PathCategory = ProcessPathCategory.PackagedTool,
        LaunchMechanism = ProcessLaunchMechanism.MxcSpawn,
        CreationStatusAvailable = false,
        CreateProcessSucceeded = null,
        CreateProcessWin32Error = null,
        SdkSpawnSucceeded = true,
        JobConfigured = null,
        JobAssigned = null,
        ResumeSucceeded = null,
    };

    private static ProcessExitResult CreateExitResult(int exitCode) => new()
    {
        ExitCode = exitCode,
        UnsignedNtStatus = exitCode < 0 ? $"0x{unchecked((uint)exitCode):X8}" : null,
        TimedOut = false,
        OutputMetadata = null,
    };

    private sealed class WrapperFiles : IDisposable
    {
        public WrapperFiles()
        {
            BaseDirectory = Path.Combine(
                Environment.CurrentDirectory,
                "TestResults",
                $"copilot-wrapper-{Guid.NewGuid():N}");
            var native = Directory.CreateDirectory(Path.Combine(BaseDirectory, "runtime")).FullName;
            CopilotPath = Path.Combine(native, "copilot.exe");
            WrapperPath = Path.Combine(native, "phantom-copilot-wrapper.exe");
            LaunchRoot = Path.Combine(BaseDirectory, "launch");
            File.WriteAllText(CopilotPath, "cli");
            File.WriteAllText(WrapperPath, "wrapper");
            Store = new CopilotLaunchPolicyStore(LaunchRoot, TimeProvider.System);
        }

        public string BaseDirectory { get; }
        public string CopilotPath { get; }
        public string WrapperPath { get; }
        public string LaunchRoot { get; }
        public CopilotLaunchPolicyStore Store { get; }

        public void Dispose() => Directory.Delete(BaseDirectory, recursive: true);

        internal sealed class TestDirectory : IDisposable
        {
            internal TestDirectory(string prefix)
            {
                Path = System.IO.Path.Combine(
                    Environment.CurrentDirectory,
                    "TestResults",
                    $"{prefix}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose() => Directory.Delete(Path, recursive: true);
        }
    }
}
