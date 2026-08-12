using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// A Windows ConPTY-backed <see cref="IPseudoTerminal"/>. The child process is launched with
/// <c>STARTUPINFOEXW</c> + <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> so interactive programs,
/// VT sequences, and control characters work correctly.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConPtyPseudoTerminal : IPseudoTerminal
{
    // ── P/Invoke ────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
        uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(SafePseudoConsoleHandle hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateNamedPipeW(
        string lpName, uint dwOpenMode, uint dwPipeMode,
        uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize,
        uint nDefaultTimeOut, ref SECURITY_ATTRIBUTES lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(SafeProcessHandle hProcess, out uint pdwHandleCount);

    // ── Constants ───────────────────────────────────────────────────────────

    private const uint EXTENDED_STARTUPINFO_PRESENT  = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT    = 0x00000400;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint HANDLE_FLAG_INHERIT           = 0x00000001;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    private const uint PIPE_ACCESS_INBOUND = 0x00000001;
    private const uint PIPE_ACCESS_OUTBOUND = 0x00000002;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // ── Structures ──────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    // ── SafeHandle Implementations ─────────────────────────────────────────

    private sealed class SafePseudoConsoleHandle : SafeHandle
    {
        public SafePseudoConsoleHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public SafePseudoConsoleHandle(IntPtr h) : base(IntPtr.Zero, ownsHandle: true) { SetHandle(h); }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() { ClosePseudoConsole(handle); return true; }
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    private readonly SafePseudoConsoleHandle _hPC;
    private readonly SafeProcessHandle _hProcess;
    private readonly SafeWaitHandle _hThread;
    private readonly FileStream _rawOutput;
    private readonly FileStream _rawInput;
    private readonly CancellationTokenSource _readyCts = new();
    private readonly Task<byte[]> _firstOutputReady;
    private bool _disposed;

    public Stream Output { get; }
    public Stream Input { get; }

    internal uint ProcessId { get; }

    /// <summary>
    /// Returns the current handle count of the child process, or <c>0</c> if the child has
    /// already exited. Uses the kernel handle directly so the result is valid even after the
    /// <see cref="System.Diagnostics.Process"/> object would have lost its snapshot.
    /// </summary>
    internal uint GetChildHandleCount()
    {
        GetProcessHandleCount(_hProcess, out uint count);
        return count;
    }

    // ── Constructor ─────────────────────────────────────────────────────────

    public ConPtyPseudoTerminal(ShellOpenPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // PTY input pipe: ConPTY reads from inputPtySide (synchronous), caller writes to inputWrite (overlapped/async)
        var (inputPtySide, inputWrite) = CreateOverlappedPtyPipe(callerReads: false);

        // PTY output pipe: ConPTY writes to outputPtySide (synchronous), caller reads from outputRead (overlapped/async)
        SafeFileHandle outputPtySide, outputRead;
        try
        {
            (outputPtySide, outputRead) = CreateOverlappedPtyPipe(callerReads: true);
        }
        catch
        {
            inputPtySide.Dispose();
            inputWrite.Dispose();
            throw;
        }

        var size = new COORD { X = (short)payload.Columns, Y = (short)payload.Rows };
        int hr = CreatePseudoConsole(size, inputPtySide, outputPtySide, 0, out IntPtr rawHpc);

        // The PTY now owns the pipe ends it was given; dispose our copies
        inputPtySide.Dispose();
        outputPtySide.Dispose();

        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(hr, "CreatePseudoConsole failed.");
        }

        _hPC = new SafePseudoConsoleHandle(rawHpc);

        IntPtr attrList = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        SafeProcessHandle? hProcess = null;
        SafeWaitHandle? hThread = null;
        try
        {
            bool refAdded = false;
            _hPC.DangerousAddRef(ref refAdded);
            try
            {
                attrList = BuildAttributeList(_hPC.DangerousGetHandle());

                var startupInfo = new STARTUPINFOEXW
                {
                    StartupInfo = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOEXW>() },
                    lpAttributeList = attrList,
                };

                string commandLine = BuildCommandLine(payload);

                if (!CreateProcessW(
                        null,
                        commandLine,
                        IntPtr.Zero, IntPtr.Zero,
                        false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        IntPtr.Zero,
                        payload.WorkingDirectory,
                        ref startupInfo,
                        out pi))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessW failed for '{commandLine}'.");
                }

                // Wrap raw handles into SafeHandles immediately
                hProcess = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
                hThread = new SafeWaitHandle(pi.hThread, ownsHandle: true);
            }
            finally
            {
                if (refAdded) _hPC.DangerousRelease();
            }
        }
        catch
        {
            hProcess?.Dispose();
            hThread?.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }

        _hProcess = hProcess;
        _hThread = hThread;
        ProcessId = pi.dwProcessId;

        // Caller-side pipe handles are created with FILE_FLAG_OVERLAPPED. Use isAsync: true
        // so FileStream uses true async I/O with deterministic cancellation and ordering.
        _rawOutput = new FileStream(outputRead, FileAccess.Read, bufferSize: 4096, isAsync: true);
        _rawInput = new FileStream(inputWrite, FileAccess.Write, bufferSize: 4096, isAsync: true);

        // Deterministic startup barrier for issue #1282:
        //
        // CreatePseudoConsole and CreateProcessW both return synchronously, but the ConPTY
        // pipeline (conhost/openconsole server thread, and the child's console-input reader)
        // reaches steady state asynchronously. On slow/headless CI runners the window between
        // "constructor returned" and "child is actually reading CONIN$" is long enough that
        // the caller's first writes to Input can be dropped before anything is attached to
        // consume them, and the child then sits idle forever.
        //
        // We issue a single overlapped read on the caller-side output pipe here. Its completion
        // is the definitive signal that the ConPTY pipeline is running end-to-end (conhost is
        // producing output, and — for interactive shells like cmd.exe — the child has attached
        // and started emitting a banner/prompt). Input.WriteAsync waits on this task before
        // touching the input pipe. The bytes we prefetch are surfaced to the caller as the
        // first bytes of Output so nothing is lost.
        //
        // No Task.Delay / Thread.Sleep: the barrier is purely event-driven.
        _firstOutputReady = ReadFirstOutputAsync(_readyCts.Token);

        Output = new PrependingReadStream(this);
        Input = new GatedWriteStream(this);
    }

    private async Task<byte[]> ReadFirstOutputAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            int n = await _rawOutput.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n <= 0)
                return Array.Empty<byte>();
            var trimmed = new byte[n];
            Buffer.BlockCopy(buf, 0, trimmed, 0, n);
            return trimmed;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<byte>();
        }
        catch
        {
            // If the read fails (e.g. pipe already broken because the child died), we still
            // release the barrier so callers see the actual downstream error rather than
            // deadlocking on the readiness wait.
            return Array.Empty<byte>();
        }
    }

    // ── IPseudoTerminal ─────────────────────────────────────────────────────

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        var size = new COORD { X = (short)columns, Y = (short)rows };
        ResizePseudoConsole(_hPC, size);
        return ValueTask.CompletedTask;
    }

    public Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitHandle = new ManualResetEvent(false);
        waitHandle.SafeWaitHandle = new SafeWaitHandle(_hProcess.DangerousGetHandle(), ownsHandle: false);

        RegisteredWaitHandle? registration = null;
        CancellationTokenRegistration cancellationRegistration = default;
        int completed = 0;

        void Complete(Action completeTask)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
            {
                return;
            }

            registration?.Unregister(null);
            cancellationRegistration.Dispose();
            waitHandle.Dispose();
            completeTask();
        }

        registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, timedOut) =>
            {
                GetExitCodeProcess(_hProcess, out uint code);
                GC.KeepAlive(_hProcess);
                Complete(() => tcs.TrySetResult((int)code));
            },
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: true);

        cancellationRegistration = ct.Register(() =>
        {
            Complete(() => tcs.TrySetCanceled(ct));
        });

        return tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Release the readiness barrier so any pending Input.WriteAsync callers unblock
        // instead of waiting forever for output that will never arrive.
        _readyCts.Cancel();
        try { await _firstOutputReady.ConfigureAwait(false); } catch { }

        await Input.DisposeAsync().ConfigureAwait(false);
        await Output.DisposeAsync().ConfigureAwait(false);

        _hPC.Dispose();
        _hThread.Dispose();
        _hProcess.Dispose();
        _readyCts.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a pipe pair where the ConPTY-owned end is synchronous (as required by ConPTY)
    /// and the caller-owned end is overlapped/async (created with FILE_FLAG_OVERLAPPED).
    /// Uses a uniquely named pipe via CreateNamedPipeW/CreateFileW to enable mixed sync/async modes.
    /// </summary>
    private static (SafeFileHandle PtySide, SafeFileHandle CallerSide) CreateOverlappedPtyPipe(
        bool callerReads)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = 0  // not inheritable
        };

        // Create a unique name for this pipe. ConPTY does not care about the pipe implementation
        // as long as it can read/write synchronously from its end.
        var pipeName = $@"\\.\pipe\ConPTY-{Guid.NewGuid():N}";

        SafeFileHandle serverHandle, clientHandle;

        if (callerReads)
        {
            // ConPTY writes (server, synchronous), caller reads (client, overlapped)
            serverHandle = CreateNamedPipeW(
                pipeName,
                PIPE_ACCESS_OUTBOUND,  // server writes
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1, 4096, 4096, 0, ref sa);

            if (serverHandle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateNamedPipeW failed (output server).");

            clientHandle = CreateFileW(
                pipeName,
                GENERIC_READ,  // client reads
                0, ref sa,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,  // async
                IntPtr.Zero);

            if (clientHandle.IsInvalid)
            {
                serverHandle.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW failed (output client).");
            }

            return (PtySide: serverHandle, CallerSide: clientHandle);
        }
        else
        {
            // ConPTY reads (server, synchronous), caller writes (client, overlapped)
            serverHandle = CreateNamedPipeW(
                pipeName,
                PIPE_ACCESS_INBOUND,  // server reads
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1, 4096, 4096, 0, ref sa);

            if (serverHandle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateNamedPipeW failed (input server).");

            clientHandle = CreateFileW(
                pipeName,
                GENERIC_WRITE,  // client writes
                0, ref sa,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,  // async
                IntPtr.Zero);

            if (clientHandle.IsInvalid)
            {
                serverHandle.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW failed (input client).");
            }

            return (PtySide: serverHandle, CallerSide: clientHandle);
        }
    }

    /// <summary>
    /// Builds a PROC_THREAD_ATTRIBUTE_LIST containing PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.
    /// The caller must free the returned list with DeleteProcThreadAttributeList+FreeHGlobal
    /// after CreateProcessW returns.
    /// </summary>
    private static IntPtr BuildAttributeList(IntPtr hPC)
    {
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        IntPtr list = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
        }

        // Pass hPC directly as lpValue — for PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE the kernel
        // takes the HPCON handle value from lpValue itself (not a pointer-to-pointer). This
        // matches Microsoft's ConPTY sample (EchoCon) and every known working C# implementation.
        if (!UpdateProcThreadAttribute(
                list, 0,
                (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero, IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }

        return list;
    }

    private static string BuildCommandLine(ShellOpenPayload payload)
    {
        var sb = new StringBuilder();
        AppendArg(sb, payload.Command);
        foreach (var arg in payload.CommandArguments)
        {
            sb.Append(' ');
            AppendArg(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendArg(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            sb.Append(arg);
        }
        else
        {
            sb.Append('"');
            foreach (char c in arg)
            {
                if (c == '"') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('"');
        }
    }

    // ── Startup-race gating streams (issue #1282) ───────────────────────────

    /// <summary>
    /// Wraps the raw ConPTY output <see cref="FileStream"/> and prepends the bytes read by the
    /// startup-readiness probe (see <see cref="ReadFirstOutputAsync"/>) so callers observe the
    /// output stream as if no bytes had been consumed. Subsequent reads delegate to the
    /// underlying overlapped <see cref="FileStream"/>.
    /// </summary>
    private sealed class PrependingReadStream : Stream
    {
        private readonly ConPtyPseudoTerminal _owner;
        private byte[]? _prepend;
        private int _prependOffset;
        private bool _prependConsumed;

        public PrependingReadStream(ConPtyPseudoTerminal owner) { _owner = owner; }

        public override bool CanRead => _owner._rawOutput.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prependConsumed)
            {
                _prepend ??= await _owner._firstOutputReady.WaitAsync(cancellationToken).ConfigureAwait(false);
                int remaining = _prepend.Length - _prependOffset;
                if (remaining > 0)
                {
                    int take = Math.Min(buffer.Length, remaining);
                    _prepend.AsSpan(_prependOffset, take).CopyTo(buffer.Span);
                    _prependOffset += take;
                    return take;
                }
                _prependConsumed = true;
            }
            return await _owner._rawOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _owner._rawOutput.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _owner._rawOutput.DisposeAsync();
    }

    /// <summary>
    /// Wraps the raw ConPTY input <see cref="FileStream"/> and gates every write on the
    /// startup-readiness signal. Once the ConPTY pipeline has produced its first output byte
    /// (proving the server thread and — for interactive shells — the child are attached and
    /// ready) writes pass straight through to the underlying overlapped <see cref="FileStream"/>.
    /// This eliminates the observable "first keystrokes dropped" race on slow runners.
    /// </summary>
    private sealed class GatedWriteStream : Stream
    {
        private readonly ConPtyPseudoTerminal _owner;

        public GatedWriteStream(ConPtyPseudoTerminal owner) { _owner = owner; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => _owner._rawInput.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush() => _owner._rawInput.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _owner._rawInput.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _owner._firstOutputReady.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _owner._rawInput.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _owner._rawInput.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _owner._rawInput.DisposeAsync();
    }
}
