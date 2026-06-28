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

        var psi = VsCodeCliLocator.BuildProcessStartInfo(@"C:\VS Code\bin\code.cmd", "tunnel status");
        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Contains(@"C:\VS Code\bin\code.cmd", psi.Arguments);
        Assert.Contains("tunnel status", psi.Arguments);
    }

    [Fact]
    public void BuildProcessStartInfo_NonCmdFile_UsesDirectExecution()
    {
        var psi = VsCodeCliLocator.BuildProcessStartInfo("/usr/bin/code", "tunnel status");
        Assert.Equal("/usr/bin/code", psi.FileName);
        Assert.Equal("tunnel status", psi.Arguments);
    }

    [Fact]
    public void BuildProcessStartInfo_AnyFile_HasRedirectOutputAndNoShellExecute()
    {
        var psi = VsCodeCliLocator.BuildProcessStartInfo("/usr/bin/code", "tunnel status");
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
    }
}
