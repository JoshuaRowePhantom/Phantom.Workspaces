using Microsoft.Extensions.Logging;
using System.Text.Json;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RunVsCodeTunnelToolTests
{
    private sealed class FakeExecutionContextProvider : ICurrentExecutionContextProvider
    {
        public string ComputerName => "test-machine";
        public string UserName => "test-user";
        public string OperatingSystemName => "windows";
        public string HomeDirectoryPath => "C:/Users/test-user";
    }

    private sealed class CliCall
    {
        public string CliPath { get; init; } = "";
        public string Arguments { get; init; } = "";
        public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    }

    private WorkspaceToolExecutionContext Context(
        string? cliPath = null,
        string? tunnelName = null)
    {
        var props = new List<string>
        {
            "\"entity-types\": [\"entity\", \"tool\"]",
            "\"tool-type\": \"run-vscode-tunnel\"",
        };

        if (cliPath is not null)
        {
            props.Add($"\"{RunVsCodeTunnelTool.CliPathProperty}\": {JsonSerializer.Serialize(cliPath)}");
        }

        if (tunnelName is not null)
        {
            props.Add($"\"{RunVsCodeTunnelTool.TunnelNameProperty}\": {JsonSerializer.Serialize(tunnelName)}");
        }

        var toolJson = "{" + string.Join(", ", props) + "}";
        return WorkspaceToolExecutionContextTestFactory.Create(
            new Phantom.Workspaces.Data.Offline.InMemoryDataAccessLayer(),
            toolJson);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceRunning_NoCliInvocations()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                return Task.FromResult(("service is running", 0));
            });

        await tool.ExecuteAsync(this.Context());

        Assert.Equal(2, calls.Count);
        Assert.Equal("tunnel service status", calls[0].Arguments);
        Assert.Equal("tunnel status", calls[1].Arguments);
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("uninstall"));
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("install"));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceNotInstalled_InstallCalled()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                // First status check returns 1 (not installed), login returns 0 (success), install returns 0
                // Post-install status check returns running
                if (args.Contains("status") && calls.Count == 1) return Task.FromResult(("", 1));
                if (args.Contains("status")) return Task.FromResult(("service is running", 0));
                return Task.FromResult(("", 0));
            },
            tokenResolver: () => "fake-token");

        await tool.ExecuteAsync(this.Context());

        Assert.True(calls.Count >= 3);
        Assert.Equal("tunnel service status", calls[0].Arguments);
        Assert.Equal("tunnel user login --provider github", calls[1].Arguments);
        Assert.Contains("tunnel service install --accept-server-license-terms --name", calls[2].Arguments);
        Assert.Contains("test-machine", calls[2].Arguments);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceStopped_UninstallThenInstallCalled()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                return Task.FromResult(("service is stopped", 0));
            },
            tokenResolver: () => "fake-token");

        await tool.ExecuteAsync(this.Context());

        Assert.Equal(5, calls.Count);
        Assert.Equal("tunnel service status", calls[0].Arguments);
        Assert.Equal("tunnel service uninstall", calls[1].Arguments);
        Assert.Equal("tunnel user login --provider github", calls[2].Arguments);
        Assert.Contains("tunnel service install --accept-server-license-terms --name", calls[3].Arguments);
        Assert.Equal("tunnel service status", calls[4].Arguments);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceRunning_HistoricalLogText_DoesNotFalselyTriggerReinstall()
    {
        // Verifies that output from "tunnel service status" containing "running" in a historical/
        // past-tense context (e.g. "service was running on port 3000") is still classified as
        // Running, so no uninstall+reinstall cycle is triggered.  This confirms the design
        // invariant: the switch to the status command makes detection reliable by key-word
        // presence alone, regardless of surrounding grammatical context.
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                return Task.FromResult(args switch
                {
                    "tunnel service status" => ("service was running on port 3000", 0),
                    _ => ("", 0)
                });
            });

        await tool.ExecuteAsync(this.Context());

        Assert.DoesNotContain(calls, c => c.Arguments == "tunnel service uninstall");
        Assert.DoesNotContain(calls, c => c.Arguments.StartsWith("tunnel service install", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceStopped_ExitZeroWithoutRunning_MapsToStopped()
    {
        // Verifies that exit 0 with no "running" keyword triggers uninstall+reinstall (Stopped path),
        // distinct from exit non-zero which triggers install-only (NotInstalled path).
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                return Task.FromResult(("", 0));
            },
            tokenResolver: () => "fake-token");

        await tool.ExecuteAsync(this.Context());

        Assert.Contains(calls, c => c.Arguments == "tunnel service uninstall");
        Assert.Contains(calls, c => c.Arguments.StartsWith("tunnel service install", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_CliPathOverride()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                return Task.FromResult(("service is running", 0));
            });

        await tool.ExecuteAsync(this.Context(cliPath: "/custom/code"));

        Assert.All(calls, c => Assert.Equal("/custom/code", c.CliPath));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_TunnelNameOverride()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                // status returns 1 (not installed), login returns 0 (success), install returns 0
                if (args.Contains("status")) return Task.FromResult(("", 1));
                return Task.FromResult(("", 0));
            },
            tokenResolver: () => "fake-token");

        await tool.ExecuteAsync(this.Context(tunnelName: "my-custom-tunnel"));

        var installCall = calls.FirstOrDefault(c => c.Arguments.Contains("install"));
        Assert.NotNull(installCall);
        Assert.Contains("my-custom-tunnel", installCall!.Arguments);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_DefaultCliPath_UsesLocatorResolvedPath()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                return Task.FromResult(("service is running", 0));
            },
            defaultCliPathResolver: () => @"C:\fake\code.cmd");

        await tool.ExecuteAsync(this.Context());

        Assert.All(calls, c => Assert.Equal(@"C:\fake\code.cmd", c.CliPath));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_CliNotFound_ReturnsFailure()
    {
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => throw new InvalidOperationException($"Failed to start process: {cli}"));

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Failed to start process", result.ErrorMessage);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_InstallFails_ReturnsFailure()
    {
        var callCount = 0;
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                callCount++;
                // First call: status check exits 1 (not installed); second: login success; third: install exits 2 (failure)
                return Task.FromResult(("", callCount switch { 1 => 1, 2 => 0, _ => 2 }));
            },
            tokenResolver: () => "fake-token");

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("exit code 2", result.ErrorMessage);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_UninstallFails_ReturnsFailure()
    {
        var callCount = 0;
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                callCount++;
                // First call: status check exits 0 without "running" (stopped); second call: uninstall exits 1 (failure)
                return Task.FromResult(callCount == 1 ? ("service is stopped", 0) : ("", 1));
            });

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("exit code 1", result.ErrorMessage);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_AfterInstall_ServiceNotRunning_ReturnsFailure()
    {
        var callCount = 0;
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                callCount++;
                return Task.FromResult(callCount switch
                {
                    1 => ("", 1),                      // status: not installed (exit non-zero)
                    2 => ("", 0),                      // login: success
                    3 => ("", 0),                      // install: success
                    _ => ("service is stopped", 0),    // follow-up status: still not running
                });
            },
            tokenResolver: () => "fake-token");

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_AfterInstall_ServiceRunning_ReturnsSuccess()
    {
        var callCount = 0;
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                callCount++;
                return Task.FromResult(callCount switch
                {
                    1 => ("", 1),                    // status: not installed (exit non-zero)
                    2 => ("", 0),                    // login: success
                    3 => ("", 0),                    // install: success
                    _ => ("service is running", 0),  // follow-up status: running
                });
            },
            tokenResolver: () => "fake-token");

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_DefaultCli_InstallNonZeroExit_LogsWarning()
    {
        var testLogger = new TestLogger<RunVsCodeTunnelTool>();
        // Status check exits non-zero (→ NotInstalled) so install is attempted.
        // The install call also uses DefaultRunCliAsync and exits non-zero, which must log a Warning.
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            defaultCliPathResolver: () => "nonexistent_cli.cmd",
            tokenResolver: () => "fake-token",
            logger: testLogger);

        await tool.ExecuteAsync(this.Context());

        Assert.Contains(testLogger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceAlreadyRunning_ResultContentContainsTunnelStatusOutput()
    {
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => args switch
            {
                "tunnel service status" => Task.FromResult(("service is running", 0)),
                "tunnel status" => Task.FromResult(("Connected to tunnel: my-machine", 0)),
                _ => Task.FromResult(("", 0))
            });

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResultContent);
        Assert.Contains("Connected to tunnel: my-machine", result.ResultContent);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceAlreadyRunning_TunnelStatusFails_ResultContentFallsBack()
    {
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => args switch
            {
                "tunnel service status" => Task.FromResult(("service is running", 0)),
                "tunnel status" => Task.FromResult(("", 1)),
                _ => Task.FromResult(("", 0))
            });

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResultContent);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ServiceAlreadyRunning_TunnelStatusEmpty_ResultContentFallsBack()
    {
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => args switch
            {
                "tunnel service status" => Task.FromResult(("service is running", 0)),
                "tunnel status" => Task.FromResult(("", 0)),
                _ => Task.FromResult(("", 0))
            });

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResultContent);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_AfterInstall_ServiceRunning_ResultContentContainsTunnelStatusOutput()
    {
        var callCount = 0;
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                if (args == "tunnel status")
                    return Task.FromResult(("Connected to tunnel: test-machine", 0));
                callCount++;
                return Task.FromResult(callCount switch
                {
                    1 => ("", 1),                    // status: not installed
                    2 => ("", 0),                    // login: success
                    3 => ("", 0),                    // install: success
                    _ => ("service is running", 0),  // follow-up status: running
                });
            },
            tokenResolver: () => "fake-token");

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResultContent);
        Assert.Contains("Connected to tunnel: test-machine", result.ResultContent);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_AfterReinstall_ServiceRunning_ResultContentContainsTunnelStatusOutput()
    {
        var callCount = 0;
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                if (args == "tunnel status")
                    return Task.FromResult(("Connected to tunnel: test-machine", 0));
                callCount++;
                return Task.FromResult(callCount switch
                {
                    1 => ("service is stopped", 0),  // status: stopped
                    2 => ("", 0),                    // uninstall: success
                    3 => ("", 0),                    // login: success
                    4 => ("", 0),                    // install: success
                    _ => ("service is running", 0),  // follow-up status: running
                });
            },
            tokenResolver: () => "fake-token");

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResultContent);
        Assert.Contains("Connected to tunnel: test-machine", result.ResultContent);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_DefaultCli_ServiceLogNonZeroExit_LogsWarning()
    {
        var testLogger = new TestLogger<RunVsCodeTunnelTool>();
        // nonexistent_cli.cmd ends with .cmd, so BuildRunProcessParameters wraps it with
        // cmd.exe /c, which exits non-zero (file not found). cmd.exe writes error to the
        // redirected stderr handle so it does not bleed onto the test-host console.
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            defaultCliPathResolver: () => "nonexistent_cli.cmd",
            logger: testLogger);

        await tool.ExecuteAsync(this.Context());

        Assert.Contains(testLogger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutException_ReturnsFailureWithTimeoutMessage()
    {
        var testLogger = new TestLogger<RunVsCodeTunnelTool>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, ct) =>
            {
                if (args.Contains("login"))
                {
                    throw new TimeoutException("Process timed out");
                }
                return Task.FromResult(("", args.Contains("status") ? 1 : 0));
            },
            tokenResolver: () => "fake-token",
            logger: testLogger);

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallServiceAsync_WithToken_InvokesPreLoginWithTokenViaEnvironmentVariable()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                return Task.FromResult(("", args.Contains("status") ? 1 : 0));
            },
            tokenResolver: () => "test-github-token");

        await tool.ExecuteAsync(this.Context());

        var loginCall = calls.FirstOrDefault(c => c.Arguments == "tunnel user login --provider github");
        Assert.NotNull(loginCall);
        Assert.NotNull(loginCall!.EnvironmentVariables);
        Assert.True(loginCall.EnvironmentVariables.ContainsKey("VSCODE_CLI_ACCESS_TOKEN"));
        Assert.Equal("test-github-token", loginCall.EnvironmentVariables["VSCODE_CLI_ACCESS_TOKEN"]);
        Assert.DoesNotContain("test-github-token", loginCall.Arguments);
    }

    [Fact]
    public async Task InstallServiceAsync_NoToken_FailsFastWithoutInvokingInstall()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                return Task.FromResult(("", 1));
            },
            tokenResolver: () => null);

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("GitHub", result.ErrorMessage);
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("tunnel user login"));
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("tunnel service install"));
    }

    [Fact]
    public async Task InstallServiceAsync_LoginFails_FailsFastWithoutInvokingInstall()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                if (args.Contains("login"))
                    return Task.FromResult(("insufficient_scope", 1));
                return Task.FromResult(("", args.Contains("status") ? 1 : 0));
            },
            tokenResolver: () => "token-lacking-scopes");

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        var loginCall = calls.FirstOrDefault(c => c.Arguments.Contains("login"));
        Assert.NotNull(loginCall);
        var installCall = calls.FirstOrDefault(c => c.Arguments.Contains("tunnel service install"));
        Assert.Null(installCall);
    }

    // ---- #1206: logging + reporting via shared invoker --------------------------------------

    private sealed class RecordingNotificationService : Phantom.Workspaces.Services.Notifications.INotificationService
    {
        private readonly List<Phantom.Workspaces.Services.Notifications.Notification> calls = [];
        public IReadOnlyList<Phantom.Workspaces.Services.Notifications.Notification> Calls
        {
            get { lock (this.calls) { return this.calls.ToArray(); } }
        }
        public IReadOnlyList<Phantom.Workspaces.Services.Notifications.NotificationEntry> Notifications => [];
        public bool HasActiveRun => false;
#pragma warning disable CS0067
        public event System.EventHandler? NotificationsChanged;
#pragma warning restore CS0067
        public void Notify(Phantom.Workspaces.Services.Notifications.Notification n)
        {
            lock (this.calls) { this.calls.Add(n); }
        }
        public void Remove(string tabId) { }
        public void MarkRead(string tabId) { }
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_InstallNonZeroExit_FailureResultContainsCliOutput()
    {
        var calls = new List<CliCall>();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall { CliPath = cli, Arguments = args, EnvironmentVariables = env });
                if (args.Contains("service status")) return Task.FromResult(("", 1));  // NotInstalled
                if (args.Contains("user login")) return Task.FromResult(("", 0));
                if (args.Contains("service install")) return Task.FromResult(("install-stdout-marker", 2));
                return Task.FromResult(("", 0));
            },
            tokenResolver: () => "fake-token");

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("install-stdout-marker", result.ErrorMessage);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_LoginNonZeroExit_NotifiesUserWithCliOutput()
    {
        var notifier = new RecordingNotificationService();
        var invoker = new VsCodeCliInvoker(
            notificationService: notifier,
            logger: null,
            processRunner: (parameters, ct) =>
            {
                var args = string.Join(" ", parameters.Arguments);
                if (args.Contains("service status") && args.Contains("tunnel"))
                    return Task.FromResult(new ProcessResult(1, "", "", ""));  // NotInstalled
                if (args.Contains("user login"))
                    return Task.FromResult(new ProcessResult(1, "login-stdout-x", "login-stderr-y", ""));
                return Task.FromResult(new ProcessResult(0, "", "", ""));
            });
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            cliInvoker: invoker,
            tokenResolver: () => "fake-token");

        await tool.ExecuteAsync(this.Context());

        Assert.NotEmpty(notifier.Calls);
        Assert.Contains(notifier.Calls, c =>
            c.Description.Contains("login-stdout-x", StringComparison.Ordinal)
            || c.Description.Contains("login-stderr-y", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_AllCliInvocations_RoutedThroughSharedInvoker()
    {
        var invocations = new List<string>();
        var invoker = new VsCodeCliInvoker(
            notificationService: null,
            logger: null,
            processRunner: (parameters, ct) =>
            {
                var args = string.Join(" ", parameters.Arguments);
                invocations.Add(args);
                if (args.Contains("service status") && args.Contains("tunnel"))
                    return Task.FromResult(new ProcessResult(0, "service is stopped", "", "service is stopped"));  // Stopped → uninstall+install
                return Task.FromResult(new ProcessResult(0, "", "", ""));
            });
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            cliInvoker: invoker,
            tokenResolver: () => "fake-token");

        await tool.ExecuteAsync(this.Context());

        Assert.Contains(invocations, args => args.Contains("service status"));
        Assert.Contains(invocations, args => args.Contains("service uninstall"));
        Assert.Contains(invocations, args => args.Contains("user login"));
        Assert.Contains(invocations, args => args.Contains("service install"));
    }
}

