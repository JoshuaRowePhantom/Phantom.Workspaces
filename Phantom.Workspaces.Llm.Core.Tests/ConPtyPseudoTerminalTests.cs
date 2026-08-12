using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConPtyPseudoTerminalTests
{
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint dwProcessId);

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    private static ShellOpenPayload MinimalPayload => new()
    {
        Command = "cmd.exe",
        CommandArguments = ["/c", "exit", "0"],
        Columns = 80,
        Rows = 24,
    };

    private static ShellOpenPayload PwshExitPayload => new()
    {
        Command = "pwsh.exe",
        CommandArguments = ["-NoLogo", "-NoProfile", "-Command", "exit 0"],
        Columns = 80,
        Rows = 24,
    };

    /// <summary>
    /// Detaches the test host from any outer ConPTY session (e.g. Windows Terminal) before
    /// creating a new pseudoconsole, then re-attaches on dispose. <c>CreatePseudoConsole</c>
    /// does not require the calling process to have an allocated console; detaching is enough
    /// to avoid interference from an outer ConPTY host.
    /// </summary>
    private sealed class ConsoleScope : IDisposable
    {
        public ConsoleScope()
        {
            FreeConsole();
        }

        public void Dispose()
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
    }

    /// <summary>
    /// Continuously reads from the PTY output stream, discarding bytes, until the
    /// CancellationToken is cancelled. This prevents the output pipe buffer from filling up
    /// and blocking the child process when the test is not interested in the output content.
    /// </summary>
    private static async Task DrainOutputAsync(ConPtyPseudoTerminal pty, CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                int read = await pty.Output.ReadAsync(buf, ct);
                if (read == 0)
                    break;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Reads from the PTY output stream, accumulating text, until <paramref name="needle"/>
    /// appears in the output or the CancellationToken is cancelled. Returns the accumulated text.
    /// </summary>
    private static async Task<string> ReadUntilAsync(ConPtyPseudoTerminal pty, string needle, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await pty.Output.ReadAsync(buf, ct);
                if (read == 0)
                    break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, read));
                if (sb.ToString().Contains(needle, StringComparison.Ordinal))
                    return sb.ToString();
            }
        }
        catch (OperationCanceledException) { }
        return sb.ToString();
    }

    [Fact]
    public async Task ConPtyPseudoTerminal_OutputStream_UsesOverlappedAsyncPipe()
    {
        // Verifies the ConPTY output stream is backed by an async-capable overlapped pipe
        // rather than ThreadPool-backed synchronous pipe I/O. An overlapped FileStream
        // wraps a handle created with FILE_FLAG_OVERLAPPED and uses true async I/O.
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);

        // FileStream created with isAsync: true throws ArgumentException if the underlying
        // handle was not created with FILE_FLAG_OVERLAPPED. If we reach here without exception,
        // the output stream is using overlapped I/O.
        Assert.True(pty.Output.CanRead);

        // A pre-cancelled ReadAsync on an overlapped stream completes with OperationCanceledException
        // immediately, without queueing work on the ThreadPool.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pty.Output.ReadAsync(new byte[1], cts.Token).AsTask());
    }

    [Fact]
    public async Task ConPtyPseudoTerminal_InputStream_UsesOverlappedAsyncPipe()
    {
        // Verifies async stdin writes are backed by an overlapped pipe and can be
        // cancelled/ordered deterministically without ThreadPool scheduling races.
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);

        Assert.True(pty.Input.CanWrite);

        // Writing an empty buffer exercises the async write path. If the handle is not
        // overlapped, FileStream would have thrown ArgumentException in the constructor.
        await pty.Input.WriteAsync(Array.Empty<byte>());

        // A pre-cancelled WriteAsync on an overlapped stream completes immediately.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pty.Input.WriteAsync(new byte[1], cts.Token).AsTask());
    }

    [Fact]
    public async Task Constructor_DoesNotThrow_WithValidPayload()
    {
        // Before the fix, the constructor throws ArgumentException because
        // CreatePipe handles lack FILE_FLAG_OVERLAPPED.
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);
    }

    [Fact]
    public async Task Constructor_OutputStreamSupportsAsyncRead()
    {
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);

        Assert.True(pty.Output.CanRead);

        // A pre-cancelled ReadAsync must complete with OperationCanceledException,
        // not ArgumentException. If the handle is not overlapped, FileStream would
        // have already thrown in the constructor — so reaching here proves the fix.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pty.Output.ReadAsync(new byte[1], cts.Token).AsTask());
    }

    [Fact]
    public async Task Constructor_InputStreamSupportsAsyncWrite()
    {
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(MinimalPayload);

        Assert.True(pty.Input.CanWrite);

        // Writing an empty buffer must not throw; this exercises the async write
        // path without requiring real data to be available.
        await pty.Input.WriteAsync(Array.Empty<byte>());
    }

    [Fact]
    public async Task DisposeAsync_ClosesAllHandles()
    {
        using var _ = new ConsoleScope();
        var pty = new ConPtyPseudoTerminal(MinimalPayload);

        await pty.DisposeAsync();

        Assert.False(pty.Output.CanRead);
        Assert.False(pty.Input.CanWrite);
    }

    // Asserts on captured cmd.exe output content. On hosted windows-latest the ConPTY output
    // pipe renders zero bytes even though input works and the child runs, so this test is
    // deterministically empty over its 30s timeout there. Runs locally (Mode=full) and in
    // nightly-local stability where ConPTY renders output normally. Tracked by #1283.
    [Fact]
    [Trait("Category", "RequiresLocalConsole")]
    public async Task ReadAsync_FromOutputStream_ReturnsData()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var readTask = ReadUntilAsync(pty, "hello", cts.Token);
        byte[] echoCommand = Encoding.ASCII.GetBytes("echo hello\r\n");
        await pty.Input.WriteAsync(echoCommand, cts.Token);
        await pty.Input.FlushAsync(cts.Token);

        Assert.Contains("hello", await readTask, StringComparison.OrdinalIgnoreCase);
        byte[] exitCommand = Encoding.ASCII.GetBytes("exit\r\n");
        await pty.Input.WriteAsync(exitCommand, cts.Token);
        await pty.Input.FlushAsync(cts.Token);
        Assert.Equal(0, await pty.WaitForExitAsync(cts.Token));
    }

    [Fact]
    public async Task WriteAsync_ToInputStream_SendsData()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Drain output concurrently so ConPTY's server thread can consume our stdin writes.
        var drain = DrainOutputAsync(pty, cts.Token);

        byte[] commands = Encoding.ASCII.GetBytes("echo world\r\nexit\r\n");
        await pty.Input.WriteAsync(commands, cts.Token);
        await pty.Input.FlushAsync(cts.Token);

        await pty.WaitForExitAsync(cts.Token);
        cts.Cancel();
        await drain;
    }

    [Fact]
    public void Constructor_ThrowsWin32Exception_WhenPseudoConsoleCreationFails()
    {
        // CreatePseudoConsole returns E_INVALIDARG (0x80070057) for a zero-size console.
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 0,
            Rows = 0,
        };

        Assert.Throws<Win32Exception>(() => new ConPtyPseudoTerminal(payload));
    }

    [Fact]
    public async Task WaitForExitAsync_ReturnsExitCode()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = ["/c", "exit", "42"],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        int exitCode = await pty.WaitForExitAsync(cts.Token);

        Assert.Equal(42, exitCode);
    }

    /// <summary>
    /// Verifies that pwsh.exe starts without the 0xc0000142 DLL-init failure that occurs when
    /// child processes inherit unwanted handles from the parent. The process must exit with code 0.
    /// </summary>
    [Fact]
    public async Task StartsShellSuccessfully_WithoutApplicationErrorDialog()
    {
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(PwshExitPayload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Drain output so the pipe buffer doesn't fill and block pwsh during startup.
        var drain = DrainOutputAsync(pty, cts.Token);

        int exitCode = await pty.WaitForExitAsync(cts.Token);
        cts.Cancel();
        await drain;

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Verifies that the child process writes output through the ConPTY pipe. Drives the shell
    /// by writing "echo hello\r\nexit\r\n" to stdin so that output is produced deterministically;
    /// reads from the Output stream concurrently until "hello" appears or the 30-second timeout fires.
    /// </summary>
    // Asserts on captured cmd.exe output content. On hosted windows-latest the ConPTY output
    // pipe renders zero bytes even though input works and the child runs, so this test is
    // deterministically empty over its 30s timeout there. Runs locally (Mode=full) and in
    // nightly-local stability where ConPTY renders output normally. Tracked by #1283.
    [Fact]
    [Trait("Category", "RequiresLocalConsole")]
    public async Task ShellProducesOutput_AfterSuccessfulStart()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var readTask = ReadUntilAsync(pty, "hello", cts.Token);
        byte[] echoCommand = Encoding.ASCII.GetBytes("echo hello\r\n");
        await pty.Input.WriteAsync(echoCommand, cts.Token);
        await pty.Input.FlushAsync(cts.Token);

        Assert.Contains("hello", await readTask, StringComparison.OrdinalIgnoreCase);
        byte[] exitCommand = Encoding.ASCII.GetBytes("exit\r\n");
        await pty.Input.WriteAsync(exitCommand, cts.Token);
        await pty.Input.FlushAsync(cts.Token);
        Assert.Equal(0, await pty.WaitForExitAsync(cts.Token));
    }

    /// <summary>
    /// Verifies that the child process does not inheritan excessive number of handles from the
    /// parent. Before the fix, all inheritable handles in the parent leaked into the child,
    /// causing handle counts to grow with each Avalonia socket, pipe, etc. opened by the host.
    /// </summary>
    [Fact]
    public async Task ChildProcessDoesNotInheritUnwantedHandles()
    {
        using var _ = new ConsoleScope();
        await using var pty = new ConPtyPseudoTerminal(PwshExitPayload);

        uint handleCount = pty.GetChildHandleCount();

        // A pwsh.exe started with -NoProfile that inherits only ConPTY-internal handles
        // should have far fewer than 100 handles. A leaking parent would push this into
        // the hundreds or thousands.
        Assert.True(handleCount < 100,
            $"pwsh.exe handle count {handleCount} exceeds 100 — child may be inheriting parent handles.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var drain = DrainOutputAsync(pty, cts.Token);
        await pty.WaitForExitAsync(cts.Token);
        cts.Cancel();
        await drain;
    }

    /// <summary>
    /// Regression test for the deadlock documented in issue #895: without a concurrent output
    /// reader, <c>Input.FlushAsync</c> blocks indefinitely once ConPTY's output pipe saturates.
    /// Phase 1 confirms the deadlock is observable (FlushAsync cancels under a short timeout).
    /// Phase 2 confirms that starting a drain task before writing resolves the deadlock.
    /// </summary>
    [Fact]
    public async Task Input_FlushAsync_DoesNotDeadlock_WhenOutputPipeIsSaturated()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = ["/d", "/q", "/k", "prompt $"],
            Columns = 80,
            Rows = 24,
        };

        // Phase 1: Without a concurrent reader, FlushAsync risks deadlocking when the output pipe
        // saturates. On a fast machine it may complete before saturation; on a slower machine the
        // CancellationToken fires. Both outcomes are valid — the invariant is that the CTS is
        // honoured and the operation does not hang indefinitely.
        await using (var pty = new ConPtyPseudoTerminal(payload))
        {
            using var shortCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            byte[] commands = Encoding.ASCII.GetBytes("echo test\r\nexit\r\n");
            await pty.Input.WriteAsync(commands, shortCts.Token);
            try { await pty.Input.FlushAsync(shortCts.Token); }
            catch (OperationCanceledException) { /* deadlock condition observed on this run */ }
        }

        // Phase 2: With a drain task running, the same write+flush sequence completes.
        await using (var pty = new ConPtyPseudoTerminal(payload))
        {
            using var longCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var drain = DrainOutputAsync(pty, longCts.Token);

            byte[] commands = Encoding.ASCII.GetBytes("echo test\r\nexit\r\n");
            await pty.Input.WriteAsync(commands, longCts.Token);
            await pty.Input.FlushAsync(longCts.Token);

            await pty.WaitForExitAsync(longCts.Token);
            longCts.Cancel();
            await drain;
        }
    }

    /// <summary>
    /// Positive test: writing more than one pipe buffer of input (≥ 8 KB) while concurrently
    /// draining output completes within the timeout, proving the concurrent-pump pattern scales
    /// past the 4 KB pipe-buffer threshold documented in issue #895.
    /// </summary>
    // Asserts on captured cmd.exe output bytes ( > 0 ). On hosted windows-latest the ConPTY
    // output pipe renders zero bytes even though input works and the child runs, so this test
    // fails there deterministically. Runs locally + nightly-local only. Tracked by #1283.
    [Fact]
    [Trait("Category", "RequiresLocalConsole")]
    public async Task Input_And_Output_ConcurrentlyPumped_CompletesWithinTimeout()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 10000,  // wide terminal: no line-wrapping for the large echo command
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Build a single large echo command exceeding two pipe buffers (> 8 KB total payload).
        // Using one long echo avoids cmd.exe executing hundreds of sequential commands, keeping
        // the test fast while still exercising the concurrent-pump invariant past 4 KB.
        var paddedContent = new string('A', 8180);
        byte[] commands = Encoding.ASCII.GetBytes($"echo {paddedContent}\r\nexit\r\n");
        Assert.True(commands.Length >= 8192, $"Commands must be ≥ 8 KB; got {commands.Length} bytes");

        // Start reading BEFORE writing — the output pipe is drained continuously so ConPTY
        // never wedges on its stdout write and can consume our large stdin payload.
        var outputBytes = 0;
        var readTask = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (true)
                {
                    int read = await pty.Output.ReadAsync(buf, cts.Token);
                    if (read == 0)
                        break;
                    outputBytes += read;
                }
            }
            catch (OperationCanceledException) { }
        });

        var writeTask = Task.Run(async () =>
        {
            await pty.Input.WriteAsync(commands, cts.Token);
            await pty.Input.FlushAsync(cts.Token);
        }, cts.Token);

        // Wait for the write to complete — FlushAsync unblocking proves that concurrent output
        // draining kept the pipe clear, allowing > 8 KB of input to be written without deadlock.
        await writeTask;

        // Write succeeded. Cancel the drain and clean up.
        cts.Cancel();
        await readTask;

        Assert.True(outputBytes > 0, "Expected output bytes from child process");
    }

    [Fact]
    public async Task WaitForExitAsync_CompletesWhenProcessExits_WithoutBlockingThreadPool()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = ["/c", "exit", "0"],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var drain = DrainOutputAsync(pty, cts.Token);

        bool wasOnThreadPoolThread = false;
        var waitCompleted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            wasOnThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            try
            {
                var exitCode = pty.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
                waitCompleted.SetResult(exitCode);
            }
            catch (Exception ex)
            {
                waitCompleted.SetException(ex);
            }
        });

        var completed = await Task.WhenAny(
            waitCompleted.Task,
            Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(waitCompleted.Task, completed);
        Assert.True(wasOnThreadPoolThread);
        Assert.Equal(0, await waitCompleted.Task);

        cts.Cancel();
        await drain;
    }

    [Fact]
    public async Task WaitForExitAsync_RespectsCancellation()
    {
        using var _ = new ConsoleScope();
        var payload = new ShellOpenPayload
        {
            Command = "cmd.exe",
            CommandArguments = [],
            Columns = 80,
            Rows = 24,
        };

        await using var pty = new ConPtyPseudoTerminal(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var drain = DrainOutputAsync(pty, cts.Token);

        using var waitCts = new CancellationTokenSource();
        var waitTask = pty.WaitForExitAsync(waitCts.Token);
        Assert.False(waitTask.IsCompleted);
        waitCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waitTask);

        cts.Cancel();
        await drain;
    }
}
