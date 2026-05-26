using System.Diagnostics;
using System.Text;

namespace Phantom.Workspaces.Agent.Cli.Tests;

public sealed class AgentCliEchoProviderTests
{
    [Fact]
    public async Task Run_WithEchoProvider_RespondsToUserInput()
    {
        var repositoryRoot = FindRepositoryRoot();
        var cliExePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Agent.Cli",
            "bin",
            "Debug",
            "net10.0",
            "Phantom.Workspaces.Agent.Cli.exe");
        Assert.True(File.Exists(cliExePath), $"Expected CLI executable at '{cliExePath}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cliExePath,
                WorkingDirectory = repositoryRoot.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        var standardOutputBuilder = new StringBuilder();

        await ReadUntilContainsAsync(
            process.StandardOutput,
            standardOutputBuilder,
            "Type /exit to quit.",
            TimeSpan.FromSeconds(10));

        await process.StandardInput.WriteLineAsync("hello");
        await process.StandardInput.FlushAsync();

        await ReadUntilContainsAsync(
            process.StandardOutput,
            standardOutputBuilder,
            "assistant > hello",
            TimeSpan.FromSeconds(10));

        await process.StandardInput.WriteLineAsync("/exit");
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(30)));
        if (!ReferenceEquals(completed, waitTask))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            Assert.Fail("CLI process did not exit within timeout.");
        }

        var standardOutput = standardOutputBuilder.ToString() + await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();

        Assert.True(
            process.ExitCode == 0,
            $"Expected zero exit code. ExitCode={process.ExitCode}, stderr={standardError}");
        Assert.Contains("Echo Chat Client", standardOutput, StringComparison.Ordinal);
        Assert.Contains("assistant > hello", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", standardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_WithEchoProvider_StdinCloseWaitsForAssistantTurn()
    {
        var repositoryRoot = FindRepositoryRoot();
        var cliExePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Agent.Cli",
            "bin",
            "Debug",
            "net10.0",
            "Phantom.Workspaces.Agent.Cli.exe");
        Assert.True(File.Exists(cliExePath), $"Expected CLI executable at '{cliExePath}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cliExePath,
                WorkingDirectory = repositoryRoot.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        var standardOutputBuilder = new StringBuilder();
        await ReadUntilContainsAsync(
            process.StandardOutput,
            standardOutputBuilder,
            "Type /exit to quit.",
            TimeSpan.FromSeconds(10));

        await process.StandardInput.WriteLineAsync("hello");
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(30)));
        if (!ReferenceEquals(completed, waitTask))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            Assert.Fail("CLI process did not exit within timeout.");
        }

        var standardOutput = standardOutputBuilder.ToString() + await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();

        Assert.True(
            process.ExitCode == 0,
            $"Expected zero exit code. ExitCode={process.ExitCode}, stderr={standardError}");
        Assert.Contains("assistant > hello", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", standardError, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ReadUntilContainsAsync(
        StreamReader reader,
        StringBuilder buffer,
        string expectedText,
        TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        var singleChar = new char[1];

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            var charsRead = await reader.ReadAsync(singleChar.AsMemory(0, 1), timeoutCts.Token);
            if (charsRead == 0)
            {
                throw new InvalidOperationException($"Stream ended before seeing expected text: '{expectedText}'.");
            }

            buffer.Append(singleChar[0]);
            if (buffer.ToString().Contains(expectedText, StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}
