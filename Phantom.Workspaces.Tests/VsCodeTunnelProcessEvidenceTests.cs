using System.Diagnostics;
using System.IO;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeTunnelProcessEvidenceTests
{
    [Theory]
    [InlineData("__VSCODE_CLI_STATUS__{\"type\":\"connected\",\"link\":\"private\"}", true, false)]
    [InlineData("__VSCODE_CLI_STATUS__{\"type\":\"tokenError\",\"message\":\"private\"}", false, true)]
    [InlineData("__VSCODE_CLI_STATUS__{\"type\":\"TokenError\"}", false, false)]
    [InlineData("__VSCODE_CLI_STATUS__{\"type\":\"connected-extra\"}", false, false)]
    [InlineData("prefix __VSCODE_CLI_STATUS__{\"type\":\"connected\"}", false, false)]
    [InlineData("__VSCODE_CLI_STATUS__{\"type\":\"tokenError\"", false, false)]
    [InlineData("__VSCODE_CLI_STATUS__{\"type\":\"other\",\"message\":\"HTTP 401 Unauthorized\"}", false, false)]
    public void MachineRecords_RequireExactPrefixTagAndCompleteJson(
        string record,
        bool connected,
        bool tokenError)
    {
        var reducer = new ProcessBackedVsCodeTunnelChildProcess.EvidenceReducer();
        reducer.AcceptRecord(record);
        Assert.Equal(connected, reducer.Evidence.ConnectedSeen);
        Assert.Equal(tokenError, reducer.Evidence.TokenErrorSeen);
        Assert.False(reducer.Complete(0).UnauthorizedHttp401Seen);
        Assert.DoesNotContain("private", reducer.Complete(0).ToString());
    }

    [Theory]
    [InlineData("HTTP 401 Unauthorized", true)]
    [InlineData("http/1.1 401 unauthorized", true)]
    [InlineData("HTTP status:401 Unauthorized", true)]
    [InlineData("HTTP status client error (401 Unauthorized)", true)]
    [InlineData("\u001b[31mHTTP\t401\u001b[0m Unauthorized", true)]
    [InlineData("401 Unauthorized", false)]
    [InlineData("HTTP 401", false)]
    [InlineData("HTTP 4010 Unauthorized", false)]
    [InlineData("last_fail_reason: 401 Unauthorized", false)]
    [InlineData("{\"last_fail_reason\":\"HTTP 401 Unauthorized\"}", false)]
    [InlineData("not authorized; exit 401", false)]
    public async Task UnauthorizedFallback_RequiresOneStrictRecord(
        string record,
        bool expected)
    {
        var reducer = new ProcessBackedVsCodeTunnelChildProcess.EvidenceReducer();
        await ProcessBackedVsCodeTunnelChildProcess.DrainAsync(new StringReader(record), reducer.AcceptRecord);
        Assert.Equal(expected, reducer.Complete(23).UnauthorizedHttp401Seen);
    }

    [Theory]
    [InlineData("HTTP 401\nUnauthorized")]
    [InlineData("HTTP 401\rUnauthorized")]
    [InlineData("HTTP 401\r\nUnauthorized")]
    public async Task UnauthorizedFallback_DoesNotJoinRecords(string text)
    {
        var reducer = new ProcessBackedVsCodeTunnelChildProcess.EvidenceReducer();
        await ProcessBackedVsCodeTunnelChildProcess.DrainAsync(new StringReader(text), reducer.AcceptRecord);
        Assert.False(reducer.Complete(0).UnauthorizedHttp401Seen);
    }

    [Fact]
    public async Task UnauthorizedFallback_DoesNotJoinStreams()
    {
        var reducer = new ProcessBackedVsCodeTunnelChildProcess.EvidenceReducer();
        await Task.WhenAll(
            ProcessBackedVsCodeTunnelChildProcess.DrainAsync(new StringReader("HTTP 401"), reducer.AcceptRecord),
            ProcessBackedVsCodeTunnelChildProcess.DrainAsync(new StringReader("Unauthorized"), reducer.AcceptRecord));
        Assert.False(reducer.Complete(0).UnauthorizedHttp401Seen);
    }

    [Fact]
    public async Task OversizedRecord_IsDiscardedInsteadOfRetainedOrPartiallyMatched()
    {
        var reducer = new ProcessBackedVsCodeTunnelChildProcess.EvidenceReducer();
        await ProcessBackedVsCodeTunnelChildProcess.DrainAsync(
            new StringReader(new string('x', 17000) + " HTTP 401 Unauthorized\n"),
            reducer.AcceptRecord);
        Assert.False(reducer.Complete(0).UnauthorizedHttp401Seen);
    }

    [Fact]
    public async Task ProcessExit_WaitsForFinalBothStreamRecords()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::Out.WriteLine('__VSCODE_CLI_STATUS__{\"type\":\"tokenError\",\"message\":\"private\"}'); [Console]::Error.Write('HTTP 401 Unauthorized'); exit 0",
                },
            },
        };
        process.Start();
        using var child = new ProcessBackedVsCodeTunnelChildProcess(process);
        var result = await child.WaitForExitAndDrainAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.TokenErrorSeen);
        Assert.True(result.UnauthorizedHttp401Seen);
        Assert.DoesNotContain("private", result.ToString());
    }

    [Fact]
    public async Task Cancellation_KillsAndAwaitsOwnedProcessBeforeReturning()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" },
            },
        };
        process.Start();
        using var child = new ProcessBackedVsCodeTunnelChildProcess(process);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child.WaitForExitAndDrainAsync(cancellation.Token));
        Assert.True(process.HasExited);
    }
}
