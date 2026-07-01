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
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateNamedPipeW(
        string lpName, uint dwOpenMode, uint dwPipeMode,
        uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize,
        uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
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
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

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
    private static extern bool GetProcessHandleCount(IntPtr hProcess, out uint pdwHandleCount);

    // ── Constants ───────────────────────────────────────────────────────────

    private const uint EXTENDED_STARTUPINFO_PRESENT  = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT    = 0x00000400;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint HANDLE_FLAG_INHERIT           = 0x00000001;
    private const uint PIPE_ACCESS_INBOUND  = 0x00000001;
    private const uint PIPE_ACCESS_OUTBOUND = 0x00000002;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TYPE_BYTE       = 0x00000000;
    private const uint PIPE_READMODE_BYTE   = 0x00000000;
    private const uint PIPE_WAIT            = 0x00000000;
    private const uint OPEN_EXISTING        = 3;
    private const uint GENERIC_READ         = 0x80000000;
    private const uint GENERIC_WRITE        = 0x40000000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // ── Structures ──────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
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

    // ── Fields ──────────────────────────────────────────────────────────────

    private readonly IntPtr _hPC;
    private readonly IntPtr _hProcess;
    private readonly IntPtr _hThread;
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

        // PTY input pipe: ConPTY reads from inputRead (synchronous), caller writes to inputWrite (overlapped)
        var id = Guid.NewGuid().ToString("N");
        var (inputRead, inputWrite)   = CreateNamedPipePair($@"\\.\pipe\phantom-pty-in-{id}",  PIPE_ACCESS_INBOUND,  GENERIC_WRITE);

        // PTY output pipe: ConPTY writes to outputWrite (synchronous), caller reads from outputRead (overlapped)
        SafeFileHandle outputWrite, outputRead;
        try
        {
            (outputWrite, outputRead) = CreateNamedPipePair($@"\\.\pipe\phantom-pty-out-{id}", PIPE_ACCESS_OUTBOUND, GENERIC_READ);
        }
        catch
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw;
        }

        var size = new COORD { X = (short)payload.Columns, Y = (short)payload.Rows };
        int hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _hPC);

        // The PTY now owns the pipe ends it was given; dispose our copies
        inputRead.Dispose();
        outputWrite.Dispose();

        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(hr, "CreatePseudoConsole failed.");
        }

        IntPtr attrList = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            attrList = BuildAttributeList(_hPC);

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
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }

        _hProcess = pi.hProcess;
        _hThread = pi.hThread;
        ProcessId = pi.dwProcessId;

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
        return Task.Run(() =>
        {
            using var mre = new ManualResetEvent(false);
            mre.SafeWaitHandle = new SafeWaitHandle(_hProcess, ownsHandle: false);
            mre.WaitOne();

            GetExitCodeProcess(_hProcess, out uint code);
            return (int)code;
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await Input.DisposeAsync().ConfigureAwait(false);
        await Output.DisposeAsync().ConfigureAwait(false);

        ClosePseudoConsole(_hPC);

        if (_hThread != IntPtr.Zero) CloseHandle(_hThread);
        if (_hProcess != IntPtr.Zero) CloseHandle(_hProcess);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a named-pipe pair where the server handle is <b>synchronous</b> (required by
    /// <c>CreatePseudoConsole</c>) and the client handle is <c>FILE_FLAG_OVERLAPPED</c>
    /// (required by <see cref="FileStream"/> constructed with <c>isAsync: true</c>). The
    /// inherit flag is explicitly cleared on both handles to prevent accidental leakage into
    /// child processes.
    /// </summary>
    private static (SafeFileHandle Server, SafeFileHandle Client) CreateNamedPipePair(
        string name, uint serverAccess, uint clientAccess)
    {
        var server = CreateNamedPipeW(
            name,
            serverAccess,   // synchronous — CreatePseudoConsole requires non-overlapped handles
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 4096, 4096, 0, IntPtr.Zero);
        if (server.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateNamedPipeW failed for '{name}'.");

        // Explicitly clear the inherit flag so these handles are never leaked into
        // child processes, guarding against any future caller that passes bInheritHandles=true.
        SetHandleInformation(server, HANDLE_FLAG_INHERIT, 0);

        var client = CreateFileW(name, clientAccess, 0, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (client.IsInvalid)
        {
            server.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFileW failed for '{name}'.");
        }

        SetHandleInformation(client, HANDLE_FLAG_INHERIT, 0);

        return (server, client);
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
