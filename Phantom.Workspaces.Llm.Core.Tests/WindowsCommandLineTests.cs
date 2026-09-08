using System.Runtime.InteropServices;
using Phantom.Workspaces.Llm.Core.Tests.Secrets;
using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class WindowsCommandLineTests
{
    [WindowsFact]
    public void WindowsCommandLine_EmbeddedQuotes_RoundTrips()
    {
        var original = "say \"hello\"";
        var line = WindowsCommandLine.Build("tool", [original]);
        Assert.Equal(["tool", original], ParseCommandLine(line));
    }

    [WindowsFact]
    public void WindowsCommandLine_TrailingBackslashes_RoundTrips()
    {
        var original = @"C:\path with space\";
        var line = WindowsCommandLine.Build("tool", [original]);
        Assert.Equal(["tool", original], ParseCommandLine(line));
    }

    [WindowsFact]
    public void WindowsCommandLine_ComplexArgumentMix_RoundTrips()
    {
        string[] args = ["", "plain", "two words", "quote\"here", @"end\\slash\", "trailing\\\"", "雪"];
        var line = WindowsCommandLine.Build("tool", args);
        var parsed = ParseCommandLine(line);
        Assert.Equal(new[] { "tool" }.Concat(args).ToArray(), parsed);
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("雪", "雪")]
    public void QuoteArgument_PreservesOtherArgumentShapes(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.QuoteArgument(argument));
    }

    [Fact]
    public void Build_NullArguments_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WindowsCommandLine.Build("tool", null!));
    }

    [Fact]
    public void Build_EmptyExecutable_Throws()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("", ["arg"]));
    }

    [Fact]
    public void QuoteArgument_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WindowsCommandLine.QuoteArgument(null!));
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        var argv = NativeMethods.CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
            throw new InvalidOperationException("CommandLineToArgvW failed.");
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            NativeMethods.LocalFree(argv);
        }
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr hMem);
    }
}
