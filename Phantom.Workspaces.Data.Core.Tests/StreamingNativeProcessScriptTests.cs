using System.Text;
using Phantom.Workspaces;

namespace Phantom.Workspaces.Data.Tests;

public sealed class StreamingNativeProcessScriptTests
{
    [Fact]
    public async Task StreamingNativeProcess_ForwardsOutputBeforeChildExits_AndRetainsBothStreams()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = FindRepositoryRoot();
        var artifacts = Path.Combine(root, "artifacts", $"streaming-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        var release = Path.Combine(artifacts, "release");
        var observedEarly = false;
        var child = $$"""
            [Console]::Out.WriteLine('EARLY')
            while (-not [IO.File]::Exists('{{Escape(release)}}')) { [Threading.Thread]::Sleep(10) }
            [Console]::Out.WriteLine('LATE')
            [Console]::Error.WriteLine('STDERR')
            exit 0
            """;
        var result = await RunAsync(root, Invoke(child) + """
            if ($result.ExitCode -ne 0) { throw "Unexpected exit $($result.ExitCode)" }
            $retained = @($result.Output | ForEach-Object { [string] $_ })
            foreach ($line in @('EARLY', 'LATE', 'STDERR')) {
                if ($line -notin $retained) { throw "Missing retained output: $line" }
            }
            if ($retained.Count -ne 3) { throw "Unexpected retained output count: $($retained.Count)" }
            Write-Output 'RETAINED_COMPLETE'
            """, line =>
        {
            if (line == "EARLY")
            {
                observedEarly = true;
                File.WriteAllText(release, "release child after forwarded output");
            }
        });

        Assert.True(observedEarly, result.StandardOut + result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("RETAINED_COMPLETE", result.StandardOut);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task StreamingNativeProcess_PreservesNativeExitCodeAndCompleteErrorOutput(int exitCode)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await RunAsync(FindRepositoryRoot(), Invoke(
            $"[Console]::Out.WriteLine('STDOUT'); [Console]::Error.WriteLine('STDERR'); exit {exitCode}") + $$"""
            if ($result.ExitCode -ne {{exitCode}}) { throw "Lost native exit: $($result.ExitCode)" }
            $retained = @($result.Output | ForEach-Object { [string] $_ })
            if ('STDOUT' -notin $retained -or 'STDERR' -notin $retained) { throw 'Lost native output' }
            if ($result.ExitCode -ne 0) {
                throw "The real production Release publish failed with exit code $($result.ExitCode)."
            }
            Write-Output 'SUCCESS'
            """);

        if (exitCode == 0)
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("SUCCESS", result.StandardOut);
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains($"The real production Release publish failed with exit code {exitCode}.",
                result.StandardOut + result.StandardError);
            Assert.DoesNotContain("SUCCESS", result.StandardOut);
        }
    }

    [Fact]
    public async Task StreamingNativeProcess_ForwardingPipelineFailurePropagates()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await RunAsync(FindRepositoryRoot(),
            "function Write-Host { throw 'FORWARDING_FAILED' }\n" +
            Invoke("[Console]::Out.WriteLine('output'); exit 0") +
            "Write-Output 'UNEXPECTED_SUCCESS'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FORWARDING_FAILED", result.StandardOut + result.StandardError);
        Assert.DoesNotContain("UNEXPECTED_SUCCESS", result.StandardOut);
    }

    [Fact]
    public async Task StreamingNativeProcess_NativeErrorPreferenceFailurePropagates()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await RunAsync(FindRepositoryRoot(),
            "$PSNativeCommandUseErrorActionPreference = $true\n" +
            Invoke("exit 9") +
            "Write-Output 'UNEXPECTED_SUCCESS'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("9", result.StandardOut + result.StandardError);
        Assert.DoesNotContain("UNEXPECTED_SUCCESS", result.StandardOut);
    }

    private static Task<ProcessResult> RunAsync(string root, string command, Action<string>? observer = null)
        => ProcessRunner.RunProcessAsync(new RunProcessParameters(
            "pwsh", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(
                "$ErrorActionPreference = 'Stop'\n" +
                $"$helper = '{Escape(Path.Combine(root, "scripts", "Invoke-StreamingNativeProcess.ps1"))}'\n" +
                command)],
            KillOnClose: KillOnCloseAction.KillTree,
            WorkingDirectory: root,
            Timeout: TimeSpan.FromSeconds(30))
        {
            WindowsTestOptions = new ProcessRunnerWindowsTestOptions { StandardOutputObserver = observer },
        });

    private static string Invoke(string child)
        => $"$result = & $helper -FileName pwsh -ProcessArguments @('-NoProfile', '-NonInteractive', '-EncodedCommand', '{Encode(child)}')\n";

    private static string Encode(string command)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Phantom.Workspaces.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not find the owning solution root.");
    }
}
