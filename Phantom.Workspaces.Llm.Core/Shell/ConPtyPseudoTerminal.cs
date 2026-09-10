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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(
        SafeJobHandle hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ── Constants ───────────────────────────────────────────────────────────

    private const uint EXTENDED_STARTUPINFO_PRESENT  = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT    = 0x00000400;
    private const uint CREATE_SUSPENDED              = 0x00000004;
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
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    // ── SafeHandle Implementations ─────────────────────────────────────────

    private sealed class SafePseudoConsoleHandle : SafeHandle
    {
        public SafePseudoConsoleHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public SafePseudoConsoleHandle(IntPtr h) : base(IntPtr.Zero, ownsHandle: true) { SetHandle(h); }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() { ClosePseudoConsole(handle); return true; }
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    private readonly SafePseudoConsoleHandle _hPC;
    private readonly SafeProcessHandle _hProcess;
    private readonly SafeWaitHandle _hThread;
    private readonly SafeJobHandle _hJob;
    private readonly TimeSpan _shutdownTimeout;
    private readonly IWindowsProcessLaunchObserver? _launchObserver;
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
        : this(new ConPtyStartOptions
        {
            Payload = payload,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        })
    {
    }

    internal static ConPtyPseudoTerminal Start(ConPtyStartOptions options) => new(options);

    private ConPtyPseudoTerminal(ConPtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var payload = options.Payload;
        ArgumentNullException.ThrowIfNull(payload);
        if (options.ShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Shutdown timeout must be positive.");
        _shutdownTimeout = options.ShutdownTimeout;
        _launchObserver = options.LaunchObserver;

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
        SafeJobHandle? hJob = null;
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
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                        IntPtr.Zero,
                        payload.WorkingDirectory,
                        ref startupInfo,
                        out pi))
                {
                    var error = Marshal.GetLastWin32Error();
                    options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
                    {
                        Stage = WindowsProcessLaunchStage.CreateProcess,
                        Succeeded = false,
                        Win32Error = error,
                    });
                    throw new Win32Exception(error, "CreateProcessW failed for the requested shell.");
                }

                // Wrap raw handles into SafeHandles immediately
                hProcess = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
                hThread = new SafeWaitHandle(pi.hThread, ownsHandle: true);
                options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
                {
                    Stage = WindowsProcessLaunchStage.CreateProcess,
                    Succeeded = true,
                    Win32Error = null,
                });

                hJob = CreateJob();
                ThrowIfInjected(options, WindowsProcessLaunchStage.ConfigureJob);
                ConfigureJob(hJob);
                options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
                {
                    Stage = WindowsProcessLaunchStage.ConfigureJob,
                    Succeeded = true,
                    Win32Error = null,
                });
                ThrowIfInjected(options, WindowsProcessLaunchStage.AssignJob);
                if (!AssignProcessToJobObject(hJob, hProcess))
                {
                    var error = Marshal.GetLastWin32Error();
                    options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
                    {
                        Stage = WindowsProcessLaunchStage.AssignJob,
                        Succeeded = false,
                        Win32Error = error,
                    });
                    throw new Win32Exception(error, "AssignProcessToJobObject failed.");
                }
                options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
                {
                    Stage = WindowsProcessLaunchStage.AssignJob,
                    Succeeded = true,
                    Win32Error = null,
                });

                ThrowIfInjected(options, WindowsProcessLaunchStage.ResumeThread);
                if (ResumeThread(hThread) == uint.MaxValue)
                {
                    var error = Marshal.GetLastWin32Error();
                    options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
                    {
                        Stage = WindowsProcessLaunchStage.ResumeThread,
                        Succeeded = false,
                        Win32Error = error,
                    });
                    throw new Win32Exception(error, "ResumeThread failed.");
                }
                options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
                {
                    Stage = WindowsProcessLaunchStage.ResumeThread,
                    Succeeded = true,
                    Win32Error = null,
                });
            }
            finally
            {
                if (refAdded) _hPC.DangerousRelease();
            }
        }
        catch
        {
            if (hProcess is not null && !hProcess.IsInvalid)
            {
                TerminateProcess(hProcess, 0xC000013A);
                WaitForSingleObject(hProcess, uint.MaxValue);
            }
            DisposeAndObserve(hJob, WindowsProcessResource.Job, options.LaunchObserver);
            DisposeAndObserve(hProcess, WindowsProcessResource.Process, options.LaunchObserver);
            DisposeAndObserve(hThread, WindowsProcessResource.Thread, options.LaunchObserver);
            inputWrite.Dispose();
            ObserveReleased(inputWrite, WindowsProcessResource.InputPipe, options.LaunchObserver);
            outputRead.Dispose();
            ObserveReleased(outputRead, WindowsProcessResource.OutputPipe, options.LaunchObserver);
            _hPC.Dispose();
            ObserveReleased(_hPC, WindowsProcessResource.PseudoConsole, options.LaunchObserver);
            throw;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
                options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
                {
                    Stage = WindowsProcessLaunchStage.ReleaseResource,
                    Succeeded = true,
                    Win32Error = null,
                    Resource = WindowsProcessResource.AttributeList,
                });
            }
        }

        _hProcess = hProcess;
        _hThread = hThread;
        _hJob = hJob;
        ProcessId = pi.dwProcessId;

        // Caller-side pipe handles are created with FILE_FLAG_OVERLAPPED. Use isAsync: true
        // so FileStream uses true async I/O with deterministic cancellation and ordering.
        Output = new FileStream(outputRead, FileAccess.Read, bufferSize: 4096, isAsync: true);
        Input = new FileStream(inputWrite, FileAccess.Write, bufferSize: 4096, isAsync: true);
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

    internal async Task<(int ExitCode, string Output)> WaitForExitAndDrainOutputAsync(
        CancellationToken cancellationToken = default)
    {
        var outputTask = DrainOutputAsync(Output, cancellationToken);
        var exitCode = await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        ReleasePseudoConsole();
        var output = await outputTask.ConfigureAwait(false);
        return (exitCode, output);
    }

    private static async Task<string> DrainOutputAsync(
        Stream output,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            output,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            text.Append(buffer, 0, count);
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        var inputHandle = ((FileStream)Input).SafeFileHandle;
        await Input.DisposeAsync().ConfigureAwait(false);
        ObserveReleased(
            inputHandle,
            WindowsProcessResource.InputPipe,
            _launchObserver);
        _hJob.Dispose();
        ObserveReleased(_hJob, WindowsProcessResource.Job, _launchObserver);

        using var shutdown = new CancellationTokenSource(_shutdownTimeout);
        try
        {
            await WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            TerminateProcess(_hProcess, 0xC000013A);
            await WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ReleasePseudoConsole();
            var outputHandle = ((FileStream)Output).SafeFileHandle;
            await Output.DisposeAsync().ConfigureAwait(false);
            ObserveReleased(
                outputHandle,
                WindowsProcessResource.OutputPipe,
                _launchObserver);
            _hThread.Dispose();
            ObserveReleased(_hThread, WindowsProcessResource.Thread, _launchObserver);
            _hProcess.Dispose();
            ObserveReleased(_hProcess, WindowsProcessResource.Process, _launchObserver);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void ThrowIfInjected(
        ConPtyStartOptions options,
        WindowsProcessLaunchStage stage)
    {
        if (options.InjectFailureAt != stage)
            return;

        const int errorGenFailure = 31;
        options.LaunchObserver?.Observe(new WindowsProcessLaunchEvent
        {
            Stage = stage,
            Succeeded = false,
            Win32Error = errorGenFailure,
        });
        throw new Win32Exception(errorGenFailure, $"Injected {stage} failure.");
    }

    private static void DisposeAndObserve(
        SafeHandle? handle,
        WindowsProcessResource resource,
        IWindowsProcessLaunchObserver? observer)
    {
        if (handle is null)
            return;
        handle.Dispose();
        ObserveReleased(handle, resource, observer);
    }

    private static void ObserveReleased(
        SafeHandle handle,
        WindowsProcessResource resource,
        IWindowsProcessLaunchObserver? observer)
    {
        observer?.Observe(new WindowsProcessLaunchEvent
        {
            Stage = WindowsProcessLaunchStage.ReleaseResource,
            Succeeded = handle.IsClosed,
            Win32Error = null,
            Resource = resource,
        });
    }

    private void ReleasePseudoConsole()
    {
        if (_hPC.IsClosed)
            return;

        _hPC.Dispose();
        ObserveReleased(_hPC, WindowsProcessResource.PseudoConsole, _launchObserver);
    }

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

    private static SafeJobHandle CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObjectW failed.");
        return job;
    }

    private static void ConfigureJob(SafeJobHandle job)
    {
        var information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        information.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf(information);
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(job, 9, pointer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
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
}
