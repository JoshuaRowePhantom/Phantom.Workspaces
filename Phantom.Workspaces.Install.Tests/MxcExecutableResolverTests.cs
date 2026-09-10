namespace Phantom.Workspaces.Install.Tests;

public sealed class MxcExecutableResolverTests
{
    [Fact]
    public void Resolve_WindowsCargoBinBeforeWindowsApps_PrefersRustExecutable()
    {
        var cargoBin = Path.Combine("C:", "Users", "test", ".cargo", "bin");
        var windowsApps = Path.Combine("C:", "Users", "test", "AppData", "Local", "Microsoft", "WindowsApps");
        var cargoExe = Path.Combine(cargoBin, "cargo.exe");
        var cargoShim = Path.Combine(windowsApps, "cargo.cmd");
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            cargoExe,
            cargoShim,
        };

        var resolved = MxcExecutableResolver.Resolve(
            "cargo",
            ["test"],
            [cargoBin, windowsApps],
            [".CMD", ".EXE"],
            isWindows: true,
            commandInterpreter: "cmd.exe",
            existingFiles.Contains);

        Assert.Equal(cargoExe, resolved.FileName, ignoreCase: true);
        Assert.Equal(["test"], resolved.Arguments);
    }

    [Fact]
    public void Resolve_WindowsCommandShim_UsesCommandInterpreter()
    {
        var tools = Path.Combine("C:", "tools");
        var shim = Path.Combine(tools, "tool.cmd");

        var resolved = MxcExecutableResolver.Resolve(
            "tool",
            ["arg one", "arg&two"],
            [tools],
            [".EXE", ".CMD"],
            isWindows: true,
            commandInterpreter: @"C:\Windows\System32\cmd.exe",
            path => string.Equals(path, shim, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\Windows\System32\cmd.exe", resolved.FileName);
        Assert.Empty(resolved.Arguments);
        var argumentString = Assert.IsType<string>(resolved.ArgumentString);
        Assert.StartsWith("/d /s /c \"\"", argumentString, StringComparison.Ordinal);
        Assert.Contains(shim, argumentString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"arg one\"", argumentString, StringComparison.Ordinal);
        Assert.Contains("\"arg&two\"", argumentString, StringComparison.Ordinal);
        Assert.EndsWith("\"\"", argumentString, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WindowsCommandShim_BuildsSpacedPathAndArguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var scriptDirectory = Path.Combine(output.Path, "command scripts");
        Directory.CreateDirectory(scriptDirectory);
        var shim = Path.Combine(scriptDirectory, "echo args.cmd");
        File.WriteAllText(shim, "@echo off\r\necho ^<%~1^>^|^<%~2^>\r\n");
        var resolved = MxcExecutableResolver.Resolve(
            "echo args",
            ["arg one", "arg two"],
            [scriptDirectory],
            [".CMD"],
            isWindows: true,
            commandInterpreter: Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            File.Exists);
        var argumentString = Assert.IsType<string>(resolved.ArgumentString);
        Assert.Contains("\"arg one\"", argumentString, StringComparison.Ordinal);
        Assert.Contains("\"arg two\"", argumentString, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WindowsCommandShim_EscapesPercentArgument()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var shim = Path.Combine(output.Path, "echo-percent.cmd");
        File.WriteAllText(shim, "@echo off\r\necho %~1\r\n");
        var resolved = MxcExecutableResolver.Resolve(
            "echo-percent",
            ["%MXC_EXECUTABLE_RESOLVER_PROBE%"],
            [output.Path],
            [".CMD"],
            isWindows: true,
            commandInterpreter: Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            File.Exists);
        var argumentString = Assert.IsType<string>(resolved.ArgumentString);
        Assert.Contains("^%MXC_EXECUTABLE_RESOLVER_PROBE^%", argumentString, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_NonWindowsCommand_FindsExtensionlessExecutable()
    {
        var tools = Path.Combine("opt", "tools");
        var executable = Path.Combine(tools, "tool");

        var resolved = MxcExecutableResolver.Resolve(
            "tool",
            [],
            [tools],
            [],
            isWindows: false,
            commandInterpreter: string.Empty,
            path => path == executable);

        Assert.Equal(executable, resolved.FileName);
        Assert.Empty(resolved.Arguments);
    }

    [Fact]
    public void Resolve_MissingPrerequisite_ThrowsExplicitFailure()
    {
        var exception = Assert.Throws<FileNotFoundException>(() =>
            MxcExecutableResolver.Resolve(
                "rustc",
                [],
                [Path.Combine("C:", "tools")],
                [".EXE", ".CMD"],
                isWindows: true,
                commandInterpreter: "cmd.exe",
                _ => false));

        Assert.Contains("rustc", exception.Message, StringComparison.Ordinal);
        Assert.Contains("PATH", exception.Message, StringComparison.Ordinal);
        Assert.Equal("rustc", exception.FileName);
    }
}
