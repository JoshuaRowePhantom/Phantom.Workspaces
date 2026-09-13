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

        var tempDir = CreateTestDirectory("cmd-shim");
        try
        {
            // Create a .cmd shim on a synthetic PATH so the resolver picks it up deterministically.
            var shimPath = Path.Combine(tempDir, "myshim.cmd");
            File.WriteAllText(shimPath, "@echo off\n");
            var environment = new StdioCommandResolver.ResolverEnvironment(
                tempDir,
                ".COM;.EXE;.BAT;.CMD",
                @"C:\Windows\System32\cmd.exe");

            var resolved = StdioCommandResolver.Resolve(
                "myshim",
                ["arg one", "arg\"two"],
                environment);

            Assert.EndsWith("cmd.exe", resolved.Executable, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, resolved.Arguments.Count);
            Assert.Equal("/d", resolved.Arguments[0]);
            Assert.Equal("/s", resolved.Arguments[1]);
            Assert.Equal("/c", resolved.Arguments[2]);
            Assert.Contains("\"arg one\"", resolved.Arguments[3]);
            Assert.Contains("arg\\\"two", resolved.Arguments[3]);
            Assert.Contains("myshim.cmd", resolved.Arguments[3]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveCommand_MissingCommand_Throws()
    {
        var tempDir = CreateTestDirectory("missing");
        try
        {
            var environment = new StdioCommandResolver.ResolverEnvironment(
                tempDir,
                ".COM;.EXE;.BAT;.CMD",
                "cmd.exe");
            Assert.Throws<FileNotFoundException>(
                () => StdioCommandResolver.Resolve("nonexistent-tool-xyz", [], environment));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string CreateTestDirectory(string suffix)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            $"stdio-command-resolver-{suffix}-{Guid.NewGuid():N}");
        return Directory.CreateDirectory(path).FullName;
    }
}
