using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Mxc.Sdk;

namespace Phantom.Workspaces.Llm.Processes;

/// <summary>MXC policy and containment already compiled for a child workload.</summary>
public sealed record MxcProcessConfiguration(
    SandboxPolicy Policy,
    SandboxContainment? Containment = null,
    string? ContainerName = null,
    bool Experimental = false);

/// <summary>Describes one streaming child-process launch.</summary>
public sealed record ProcessExecutionRequest(
    string Executable,
    IReadOnlyList<string>? Arguments = null)
{
    /// <summary>Ordered argv values, excluding the executable.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Arguments ?? [];

    /// <summary>Initial working directory, or the inherited directory when null.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment entries added to or replacing inherited values.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Maximum execution time, or null for no executor timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Optional compiled containment configuration.</summary>
    public MxcProcessConfiguration? Mxc { get; init; }
}

/// <summary>Information available immediately after launch.</summary>
public sealed record ProcessLaunchInfo(
    int? ProcessId,
    bool IsContained,
    IReadOnlyList<string> Warnings);

/// <summary>The terminal outcome and diagnostics of a child process.</summary>
public sealed record ProcessExitResult(
    int ExitCode,
    bool TimedOut,
    SandboxOutputMetadata? OutputMetadata,
    bool StandardOutputTruncated = false,
    bool StandardErrorTruncated = false);

/// <summary>An owned live process with separate standard streams.</summary>
public interface IProcessHandle : IAsyncDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    ProcessLaunchInfo LaunchInfo { get; }
    Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default);
    void Kill();
}

/// <summary>Starts ordinary or MXC-contained streaming processes.</summary>
public interface IProcessExecutor
{
    IProcessHandle Start(ProcessExecutionRequest request);
}

/// <summary>Default policy-aware streaming process executor.</summary>
public sealed class ProcessExecutor : IProcessExecutor
{
    private readonly ISystemProcessFactory systemProcessFactory;
    private readonly ISandboxRunner sandboxRunner;

    /// <summary>Creates an executor backed by System.Diagnostics.Process and MXC.</summary>
    public ProcessExecutor()
        : this(SystemProcessFactory.Instance, MxcSandboxRunner.Default)
    {
    }

    internal ProcessExecutor(
        ISystemProcessFactory systemProcessFactory,
        ISandboxRunner sandboxRunner)
    {
        this.systemProcessFactory = systemProcessFactory;
        this.sandboxRunner = sandboxRunner;
    }

    /// <inheritdoc/>
    public IProcessHandle Start(ProcessExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);
        ValidateTimeout(request.Timeout);

        IProcessBackend backend;
        if (request.Mxc is null)
        {
            backend = systemProcessFactory.Start(request);
        }
        else
        {
            var sandboxRequest = CreateSandboxRequest(request);
            backend = new SandboxProcessBackend(sandboxRunner.Spawn(sandboxRequest));
        }

        try
        {
            return new StreamingProcessHandle(backend);
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    private static SandboxRequest CreateSandboxRequest(ProcessExecutionRequest request)
    {
        var configuration = request.Mxc!;
        var policy = ClonePolicy(configuration.Policy, request.Timeout);
        var sandboxRequest = new SandboxRequest(
            policy,
            WindowsCommandLine.Build(request.Executable, request.Arguments))
        {
            WorkingDirectory = request.WorkingDirectory,
            Environment = new Dictionary<string, string>(request.Environment),
            ContainerName = configuration.ContainerName,
            Experimental = configuration.Experimental,
        };

        if (configuration.Containment is not null)
            sandboxRequest.Containment = configuration.Containment;

        return sandboxRequest;
    }

    private static SandboxPolicy ClonePolicy(SandboxPolicy source, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(source);
#pragma warning disable MXC0001
        return new SandboxPolicy
        {
            Version = source.Version,
            Filesystem = source.Filesystem,
            Network = source.Network,
            Ui = source.Ui,
            CaptureDenials = source.CaptureDenials,
            TimeoutMs = timeout is null
                ? source.TimeoutMs
                : checked((uint)Math.Ceiling(timeout.Value.TotalMilliseconds)),
        };
#pragma warning restore MXC0001
    }

    private static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is { } value
            && (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProcessExecutionRequest.Timeout),
                timeout,
                $"Timeout must be greater than zero and no more than {uint.MaxValue} milliseconds.");
        }
    }
}

internal interface ISystemProcessFactory
{
    IProcessBackend Start(ProcessExecutionRequest request);
}

internal interface IProcessBackend : IDisposable
{
    int? ProcessId { get; }
    bool IsContained { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    IReadOnlyList<string> Warnings { get; }
    bool HasExited { get; }
    Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal sealed class StreamingProcessHandle : IProcessHandle
{
    private readonly IProcessBackend backend;
    private readonly AdaptiveOutputStream stdout = new();
    private readonly AdaptiveOutputStream stderr = new();
    private readonly object pumpLock = new();
    private Task? stdoutPump;
    private Task? stderrPump;
    private bool disposed;

    public StreamingProcessHandle(IProcessBackend backend)
    {
        this.backend = backend;
        StandardInput = backend.StandardInput;
        LaunchInfo = new ProcessLaunchInfo(
            backend.ProcessId,
            backend.IsContained,
            backend.Warnings);
    }

    public Stream StandardInput { get; }
    public Stream StandardOutput
    {
        get
        {
            stdout.Observe();
            EnsurePumpsStarted();
            return stdout;
        }
    }
    public Stream StandardError
    {
        get
        {
            stderr.Observe();
            EnsurePumpsStarted();
            return stderr;
        }
    }
    public ProcessLaunchInfo LaunchInfo { get; }

    public async Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var pumps = EnsurePumpsStarted();
        var result = await backend.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(pumps.Stdout, pumps.Stderr).ConfigureAwait(false);
        return result with
        {
            StandardOutputTruncated = stdout.WasTruncated,
            StandardErrorTruncated = stderr.WasTruncated,
        };
    }

    public void Kill()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        backend.Kill();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        disposed = true;
        Exception? killError = null;
        try
        {
            if (!backend.HasExited)
                backend.Kill();
        }
        catch (Exception ex)
        {
            killError = ex;
        }

        var pumps = EnsurePumpsStarted();
        Exception? disposeError = null;
        try
        {
            StandardInput.Dispose();
            backend.Dispose();
        }
        catch (Exception ex)
        {
            disposeError = ex;
        }

        try
        {
            await Task.WhenAll(pumps.Stdout, pumps.Stderr).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        finally
        {
            stdout.Dispose();
            stderr.Dispose();
        }

        if (killError is not null)
            ExceptionDispatchInfo.Capture(killError).Throw();
        if (disposeError is not null)
            ExceptionDispatchInfo.Capture(disposeError).Throw();
    }

    private (Task Stdout, Task Stderr) EnsurePumpsStarted()
    {
        lock (pumpLock)
        {
            stdoutPump ??= PumpAsync(backend.StandardOutput, stdout);
            stderrPump ??= PumpAsync(backend.StandardError, stderr);
            return (stdoutPump, stderrPump);
        }
    }

    private static async Task PumpAsync(Stream source, AdaptiveOutputStream destination)
    {
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                var count = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0)
                    break;
                destination.WriteChunk(buffer.AsSpan(0, count));
            }

            destination.Complete();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            destination.Complete(ex);
        }
    }
}

internal sealed class AdaptiveOutputStream : Stream
{
    private const int BufferedChunkLimit = 64;
    private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private byte[]? currentChunk;
    private int currentOffset;
    private int bufferedChunks;
    private volatile bool observed;
    private bool disposed;

    internal bool WasTruncated { get; private set; }

    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal void WriteChunk(ReadOnlySpan<byte> bytes)
    {
        if (!observed && bufferedChunks >= BufferedChunkLimit && chunks.Reader.TryRead(out _))
        {
            bufferedChunks--;
            WasTruncated = true;
        }

        bufferedChunks++;
        chunks.Writer.TryWrite(bytes.ToArray());
    }

    internal void Observe() => observed = true;

    internal void Complete(Exception? error = null) => chunks.Writer.TryComplete(error);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use asynchronous reads for process output.");

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty)
            return 0;

        while (currentChunk is null || currentOffset == currentChunk.Length)
        {
            if (!await chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;
            if (!chunks.Reader.TryRead(out currentChunk))
                continue;
            bufferedChunks--;
            currentOffset = 0;
        }

        var count = Math.Min(buffer.Length, currentChunk.Length - currentOffset);
        currentChunk.AsMemory(currentOffset, count).CopyTo(buffer);
        currentOffset += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            chunks.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class SystemProcessFactory : ISystemProcessFactory
{
    public static SystemProcessFactory Instance { get; } = new();

    public IProcessBackend Start(ProcessExecutionRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (request.WorkingDirectory is not null)
            startInfo.WorkingDirectory = request.WorkingDirectory;

        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        foreach (var (name, value) in request.Environment)
            startInfo.Environment[name] = value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start process '{request.Executable}'.");
            return new SystemProcessBackend(process, request.Timeout);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class SystemProcessBackend(Process process, TimeSpan? timeout) : IProcessBackend
{
    public int? ProcessId => process.Id;
    public bool IsContained => false;
    public Stream StandardInput => process.StandardInput.BaseStream;
    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public Stream StandardError => process.StandardError.BaseStream;
    public IReadOnlyList<string> Warnings => [];
    public bool HasExited => process.HasExited;

    public async Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken)
    {
        if (timeout is null)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessExitResult(process.ExitCode, false, null);
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout.Value);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            return new ProcessExitResult(process.ExitCode, false, null);
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Kill();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new ProcessExitResult(process.ExitCode, true, null);
        }
    }

    public void Kill()
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }

    public void Dispose() => process.Dispose();
}

internal sealed class SandboxProcessBackend(ISandboxProcess process) : IProcessBackend
{
    private readonly Stream standardInput = process.StandardInput ?? Stream.Null;
    private readonly Stream standardOutput = process.StandardOutput ?? Stream.Null;
    private readonly Stream standardError = process.StandardError ?? Stream.Null;

    public int? ProcessId => process.Id is 0 ? null : checked((int)process.Id);
    public bool IsContained => true;
    public Stream StandardInput => standardInput;
    public Stream StandardOutput => standardOutput;
    public Stream StandardError => standardError;
    public IReadOnlyList<string> Warnings => process.Warnings;
    public bool HasExited => process.TryGetExitCode(out _);

    public async Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken)
    {
        var result = await process.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessExitResult(
            result.ExitCode,
            result.TimedOut,
            process.OutputMetadata);
    }

    public void Kill() => process.Kill();
    public void Dispose() => process.Dispose();
}
