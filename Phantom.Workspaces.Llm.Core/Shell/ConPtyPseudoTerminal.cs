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

        // PTY input pipe: ConPTY reads from inputPtySide (synchronous), caller writes to inputWrite (synchronous)
        var (inputPtySide, inputWrite) = CreatePtyPipe(callerReads: false);

        // PTY output pipe: ConPTY writes to outputPtySide (synchronous), caller reads from outputRead (synchronous)
        SafeFileHandle outputPtySide, outputRead;
        try
        {
            (outputPtySide, outputRead) = CreatePtyPipe(callerReads: true);
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

        // Anonymous pipes from CreatePipe are synchronous. Use isAsync: false.
        // FileStream will use threadpool-based async for ReadAsync/WriteAsync calls.
        Output = new FileStream(outputRead, FileAccess.Read, bufferSize: 4096, isAsync: false);
        Input = new FileStream(inputWrite, FileAccess.Write, bufferSize: 4096, isAsync: false);
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

        await Input.DisposeAsync().ConfigureAwait(false);
        await Output.DisposeAsync().ConfigureAwait(false);

        _hPC.Dispose();
        _hThread.Dispose();
        _hProcess.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an anonymous pipe pair (per the documented ConPTY sample). Both ends are
    /// synchronous; FileStream(isAsync:false) will use threadpool for async operations.
    /// </summary>
    private static (SafeFileHandle PtySide, SafeFileHandle CallerSide) CreatePtyPipe(
        bool callerReads)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = 0  // not inheritable
        };

        SafeFileHandle read, write;
        if (!CreatePipe(out read, out write, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");

        // PtySide = synchronous end for ConPTY, CallerSide = synchronous end for caller
        SafeFileHandle ptySide, callerSide;
        if (callerReads)
        {
            ptySide = write;      // ConPTY writes rendered output
            callerSide = read;    // caller reads
        }
        else
        {
            ptySide = read;       // ConPTY reads stdin
            callerSide = write;   // caller writes
        }

        // Explicitly clear inherit flags (belt-and-suspenders with bInheritHandle=0)
        SetHandleInformation(ptySide, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(callerSide, HANDLE_FLAG_INHERIT, 0);

        return (ptySide, callerSide);
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
}
