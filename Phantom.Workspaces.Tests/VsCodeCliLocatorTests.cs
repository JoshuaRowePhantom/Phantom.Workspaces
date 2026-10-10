using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeCliLocatorTests
{
    [Fact]
    public void ResolveDefaultCliPath_NoFilesExist_ReturnsBareCode()
    {
        var result = VsCodeCliLocator.ResolveDefaultCliPath(_ => false);
        Assert.Equal("code", result);
    }

    [Fact]
    public void ResolveDefaultCliPath_FirstCandidateExists_ReturnsFirstCandidate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var first = VsCodeCliLocator.GetWindowsCandidatePaths()[0];
        var result = VsCodeCliLocator.ResolveDefaultCliPath(p => p == first);
        Assert.Equal(first, result);
    }

    [Fact]
    public void ResolveDefaultCliPath_SecondCandidateExists_ReturnsSecondCandidate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var paths = VsCodeCliLocator.GetWindowsCandidatePaths();
        var result = VsCodeCliLocator.ResolveDefaultCliPath(p => p == paths[1]);
        Assert.Equal(paths[1], result);
    }

    [Fact]
    public void ResolveDefaultCliPath_FirstTakesPrecedenceWhenBothExist()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var paths = VsCodeCliLocator.GetWindowsCandidatePaths();
        var result = VsCodeCliLocator.ResolveDefaultCliPath(paths.Contains);
        Assert.Equal(paths[0], result);
    }

    [Fact]
    public void GetWindowsCandidatePaths_ContainsLocalAppDataAndProgramFilesPaths()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var paths = VsCodeCliLocator.GetWindowsCandidatePaths();

        Assert.Contains(paths, p => p.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetWindowsCandidatePaths_ContainsInsidersPaths()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var paths = VsCodeCliLocator.GetWindowsCandidatePaths();
        Assert.Contains(paths, p => p.Contains("Insiders", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetWindowsCandidatePaths_AllPathsEndWithCmdExtension()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var paths = VsCodeCliLocator.GetWindowsCandidatePaths();
        Assert.All(paths, p => Assert.EndsWith(".cmd", p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildProcessStartInfo_CmdFileOnWindows_WrapsWithCmdExe()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var psi = VsCodeCliLocator.BuildProcessStartInfo(@"C:\VS Code\bin\code.cmd", ["tunnel", "status"]);
        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Contains(@"C:\VS Code\bin\code.cmd", psi.Environment.Values);
        Assert.Contains("tunnel", psi.Environment.Values);
        Assert.Contains("status", psi.Environment.Values);
        Assert.Contains("/d /v:off /s /c", psi.Arguments);
    }

    [Fact]
    public void BuildProcessStartInfo_NonCmdFile_UsesDirectExecution()
    {
        var psi = VsCodeCliLocator.BuildProcessStartInfo("/usr/bin/code", ["tunnel", "status"]);
        Assert.Equal("/usr/bin/code", psi.FileName);
        Assert.Equal(["tunnel", "status"], psi.ArgumentList);
    }

    [Fact]
    public void BuildProcessStartInfo_AnyFile_HasRedirectOutputAndNoShellExecute()
    {
        var psi = VsCodeCliLocator.BuildProcessStartInfo("/usr/bin/code", ["tunnel", "status"]);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
    }

    [Fact]
    public void BuildRunProcessParameters_TimeoutProvided_SetsTimeoutField()
    {
        var timeout = TimeSpan.FromMinutes(5);
        var parameters = VsCodeCliLocator.BuildRunProcessParameters(
            "code.cmd",
            ["tunnel", "status"],
            timeout);

        Assert.Equal(timeout, parameters.Timeout);
    }

    [Fact]
    public void BuildRunProcessParameters_NoTimeoutProvided_TimeoutIsNull()
    {
        var parameters = VsCodeCliLocator.BuildRunProcessParameters(
            "code.cmd",
            ["tunnel", "status"],
            timeout: null);

        Assert.Null(parameters.Timeout);
    }

    [Fact]
    public void BuildRunProcessParameters_PathWithSpaces_PreservesSingleArgument()
    {
        const string path = @"C:\Work Folders\Example Repo";
        var parameters = VsCodeCliLocator.BuildRunProcessParameters("code.exe", [path]);
        Assert.Equal(path, Assert.Single(parameters.Arguments));
        Assert.Null(parameters.CommandInterpreterArguments);
    }

    [Fact]
    public void BuildRunProcessParameters_TunnelStatus_PreservesSeparateArguments()
    {
        var parameters = VsCodeCliLocator.BuildRunProcessParameters("code.exe", ["tunnel", "status"]);
        Assert.Equal(["tunnel", "status"], parameters.Arguments);
    }

    [Theory]
    [InlineData("unsafe\"operand")]
    [InlineData("unsafe\noperand")]
    [InlineData("unsafe\roperand")]
    [InlineData("unsafe\0operand")]
    public void BuildRunProcessParameters_CmdRejectsShellSyntaxBreakingOperands(string operand)
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Throws<ArgumentException>(() =>
            VsCodeCliLocator.BuildRunProcessParameters("code.cmd", [operand]));
    }

    [Fact]
    public void BuildRunProcessParameters_CmdPreservesEnvironmentAndTimeout()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var timeout = TimeSpan.FromMinutes(1);
        var environment = new Dictionary<string, string> { ["VSCODE_CLI_ACCESS_TOKEN"] = "test-token" };
        var parameters = VsCodeCliLocator.BuildRunProcessParameters(
            @"C:\VS Code\code.cmd", ["tunnel", "status"], timeout, environment);
        Assert.Equal(timeout, parameters.Timeout);
        Assert.Equal("test-token", parameters.EnvironmentVariables!["VSCODE_CLI_ACCESS_TOKEN"]);
        Assert.Single(environment);
        Assert.Empty(parameters.Arguments);
        Assert.NotNull(parameters.CommandInterpreterArguments);
    }
}
