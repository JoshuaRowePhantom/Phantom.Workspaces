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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes, uint nSize);

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

    // ── Constants ───────────────────────────────────────────────────────────

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

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

    // ── Constructor ─────────────────────────────────────────────────────────

    public ConPtyPseudoTerminal(ShellOpenPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // PTY input pipe: child reads from inputRead, caller writes to inputWrite
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed.");

        // PTY output pipe: caller reads from outputRead, child writes to outputWrite
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed.");
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
        IntPtr hpcValuePtr = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            (attrList, hpcValuePtr) = BuildAttributeList(_hPC);

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
                    EXTENDED_STARTUPINFO_PRESENT,
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
            if (hpcValuePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(hpcValuePtr);
            }
        }

        _hProcess = pi.hProcess;
        _hThread = pi.hThread;

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
    /// Builds a PROC_THREAD_ATTRIBUTE_LIST containing PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.
    /// Returns (attributeList, hpcValuePtr) — the caller must free both with
    /// DeleteProcThreadAttributeList+FreeHGlobal after CreateProcessW returns.
    /// </summary>
    private static (IntPtr List, IntPtr HpcValuePtr) BuildAttributeList(IntPtr hPC)
    {
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        IntPtr list = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
        }

        // Allocate a buffer that holds the HPCON value; lpValue must remain valid through CreateProcess.
        IntPtr hpcValuePtr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(hpcValuePtr, hPC);

        if (!UpdateProcThreadAttribute(
                list, 0,
                (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hpcValuePtr,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero, IntPtr.Zero))
        {
            Marshal.FreeHGlobal(hpcValuePtr);
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }

        return (list, hpcValuePtr);
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
