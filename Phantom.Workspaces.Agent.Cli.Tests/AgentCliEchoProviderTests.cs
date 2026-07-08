using System.Diagnostics;
using System.Text;

namespace Phantom.Workspaces.Agent.Cli.Tests;

public sealed class AgentCliEchoProviderTests
{
    static readonly string cliExePath = Path.Combine(
        Path.GetDirectoryName(typeof(AgentCliEchoProviderTests).Assembly.Location)!,
        "Phantom.Workspaces.Agent.Cli.exe");

    [Fact]
    public async Task Run_WithEchoProvider_RespondsToUserInput()
    {
        Assert.True(File.Exists(cliExePath), $"Expected CLI executable at '{cliExePath}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cliExePath,
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
            "Type /exit to quit.");

        await process.StandardInput.WriteLineAsync("hello");
        await process.StandardInput.FlushAsync();

        await ReadUntilContainsAsync(
            process.StandardOutput,
            standardOutputBuilder,
            "assistant > hello");

        await process.StandardInput.WriteLineAsync("/exit");
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        await process.WaitForExitAsync();

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
        Assert.True(File.Exists(cliExePath), $"Expected CLI executable at '{cliExePath}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cliExePath,
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
            "Type /exit to quit.");

        await process.StandardInput.WriteLineAsync("hello");
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        await process.WaitForExitAsync();

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
        string expectedText)
    {
        var singleChar = new char[1];

        while (true)
        {
            var charsRead = await reader.ReadAsync(singleChar.AsMemory(0, 1));
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
}
