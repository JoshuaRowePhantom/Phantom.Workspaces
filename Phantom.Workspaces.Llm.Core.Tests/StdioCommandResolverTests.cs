using Phantom.Workspaces.Llm.Mcp;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers <see cref="StdioCommandResolver"/> (issue #1477): user commands such as <c>npx</c> must
/// be resolved on PATH/PATHEXT; resolved <c>.cmd</c>/<c>.bat</c> shims must be launched through
/// <c>%ComSpec%</c> with the original arguments quoted so argv semantics are preserved.
/// </summary>
public sealed class StdioCommandResolverTests
{
    [Fact]
    public void ResolveCommand_CmdShim_UsesComSpecWithQuotedArguments()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tempDir = Directory.CreateTempSubdirectory("phantom-stdio-cmd-shim-").FullName;
        try
        {
            // Create a .cmd shim on a synthetic PATH so the resolver picks it up deterministically.
            var shimPath = Path.Combine(tempDir, "myshim.cmd");
            File.WriteAllText(shimPath, "@echo off\n");

            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var originalPathExt = Environment.GetEnvironmentVariable("PATHEXT");
            try
            {
                Environment.SetEnvironmentVariable("PATH", tempDir);
                Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");
                Environment.SetEnvironmentVariable("ComSpec", @"C:\Windows\System32\cmd.exe");

                var resolved = StdioCommandResolver.Resolve("myshim", ["arg one", "arg\"two"]);

                Assert.EndsWith("cmd.exe", resolved.Executable, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(4, resolved.Arguments.Count);
                Assert.Equal("/d", resolved.Arguments[0]);
                Assert.Equal("/s", resolved.Arguments[1]);
                Assert.Equal("/c", resolved.Arguments[2]);
                // Whitespace-containing argument is quoted; embedded quotes are backslash-escaped
                // (Windows CommandLineToArgvW round-trip).
                Assert.Contains("\"arg one\"", resolved.Arguments[3]);
                Assert.Contains("arg\\\"two", resolved.Arguments[3]);
                // The shim itself is present in the composed command line.
                Assert.Contains("myshim.cmd", resolved.Arguments[3]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                Environment.SetEnvironmentVariable("PATHEXT", originalPathExt);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveCommand_MissingCommand_Throws()
    {
        var tempDir = Directory.CreateTempSubdirectory("phantom-stdio-missing-").FullName;
        try
        {
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", tempDir);
                Assert.Throws<FileNotFoundException>(
                    () => StdioCommandResolver.Resolve("nonexistent-tool-xyz", []));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
