using System.Diagnostics;
using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tools;

public sealed class RunVsCodeTunnelTool : IWorkspaceTool
{
    public const string CliPathProperty = "cli-path";
    public const string TunnelNameProperty = "tunnel-name";
    public const string ProviderProperty = "provider";
    public static TimeSpan DefaultScheduleFrequency { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan DefaultInitialStatusCheckDelay { get; } = TimeSpan.FromSeconds(10);

    private readonly IAccessBroker accessBroker;
    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider;
    private readonly Func<string?> defaultCliPathResolver;
    private readonly VsCodeCliInvoker cliInvoker;
    private readonly IVsCodeTunnelStatusResolver tunnelStatusResolver;
    private readonly VsCodeTunnelProcessLauncher processLauncher;
    private readonly Func<CancellationToken, Task> waitBetweenPollsAsync;
    private readonly Func<CancellationToken, Task> initialStatusCheckDelayAsync;

    public RunVsCodeTunnelTool(
        IAccessBroker accessBroker,
        ICurrentExecutionContextProvider? currentExecutionContextProvider = null,
        Func<string?>? defaultCliPathResolver = null,
        VsCodeCliInvoker? cliInvoker = null,
        IVsCodeTunnelStatusResolver? tunnelStatusResolver = null,
        VsCodeTunnelProcessLauncher? processLauncher = null,
        Func<CancellationToken, Task>? waitBetweenPollsAsync = null,
        Func<CancellationToken, Task>? initialStatusCheckDelayAsync = null)
    {
        this.accessBroker = accessBroker;
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
        this.defaultCliPathResolver = defaultCliPathResolver ?? VsCodeCliLocator.ResolveDefaultCliPath;
        this.cliInvoker = cliInvoker ?? new VsCodeCliInvoker();
        this.tunnelStatusResolver = tunnelStatusResolver ?? new VsCodeTunnelStatusResolver(this.cliInvoker);
        this.processLauncher = processLauncher ?? LaunchProcess;
        this.waitBetweenPollsAsync = waitBetweenPollsAsync ?? (token => Task.Delay(TimeSpan.FromSeconds(15), token));
        this.initialStatusCheckDelayAsync = initialStatusCheckDelayAsync ?? (token => Task.Delay(DefaultInitialStatusCheckDelay, token));
    }

    public string ToolType => "run-vscode-tunnel";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        try
        {
            var cliPath = ReadProperty(context.Tool.Data, CliPathProperty) ?? this.defaultCliPathResolver();
            if (string.IsNullOrWhiteSpace(cliPath))
            {
                return Failure(VsCodeTunnelOutcome.CommandFailed);
            }

            var provider = ReadProperty(context.Tool.Data, ProviderProperty) switch
            {
                null or "github" => VsCodeTunnelProvider.GitHub,
                "microsoft" => VsCodeTunnelProvider.Microsoft,
                _ => (VsCodeTunnelProvider?)null,
            };
            if (provider is null)
            {
                return Failure(VsCodeTunnelOutcome.CommandFailed);
            }

            var request = new VsCodeTunnelStartRequest
            {
                CliPath = cliPath,
                TunnelName = ReadProperty(context.Tool.Data, TunnelNameProperty) ?? this.currentExecutionContextProvider.ComputerName,
                Provider = provider.Value,
                Requestor = new AccessRequestor
                {
                    Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
                        AccessRequestorKind.Tool,
                        context.Tool.EntityId.ToString()),
                    Kind = AccessRequestorKind.Tool,
                    SafeDisplayName = "VS Code tunnel",
                },
            };
            var status = await this.tunnelStatusResolver.ResolveAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
            if (status.Outcome is VsCodeTunnelOutcome.CommandFailed or VsCodeTunnelOutcome.InvalidStatus or VsCodeTunnelOutcome.Cancelled)
            {
                return Failure(status.Outcome);
            }

            var presence = await this.ProbePresenceAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
            if (presence == VsCodeTunnelOutcome.CommandFailed)
            {
                return Failure(presence);
            }

            if (presence == VsCodeTunnelOutcome.AuthenticationRequired
                && !await this.AuthorizeAsync(request, requiresRenewal: false, context.CancellationToken).ConfigureAwait(false))
            {
                return Failure(VsCodeTunnelOutcome.AuthenticationRequired);
            }

            var outcome = await this.RunHostAsync(request, context.CancellationToken).ConfigureAwait(false);
            if (outcome != VsCodeTunnelOutcome.AuthenticationRequired)
            {
                return Result(outcome);
            }

            if (!await this.AuthorizeAsync(request, requiresRenewal: true, context.CancellationToken).ConfigureAwait(false))
            {
                return Failure(VsCodeTunnelOutcome.AuthenticationRequired);
            }

            outcome = await this.RunHostAsync(request, context.CancellationToken).ConfigureAwait(false);
            if (outcome == VsCodeTunnelOutcome.AuthenticationRequired)
            {
                // Keep an actionable Perform row, but never automatically run another login
                // or restart this host again. A later explicit action is a new generation.
                await this.AuthorizeAsync(request, requiresRenewal: true, context.CancellationToken).ConfigureAwait(false);
                return Failure(VsCodeTunnelOutcome.AuthenticationRequired);
            }

            return Result(outcome);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return Failure(VsCodeTunnelOutcome.Cancelled);
        }
        catch (Exception)
        {
            return Failure(VsCodeTunnelOutcome.CommandFailed);
        }
    }

    private async Task<bool> AuthorizeAsync(
        VsCodeTunnelStartRequest request,
        bool requiresRenewal,
        CancellationToken cancellationToken)
    {
        var lineage = new CredentialUseLineage();
        var outcome = await this.accessBroker.AcquireAsync(new CliOwnedAuthorizationRequest
        {
            CliPath = request.CliPath,
            Kind = request.Provider == VsCodeTunnelProvider.GitHub
                ? CliOwnedAuthorizationKind.VsCodeTunnelGitHub : CliOwnedAuthorizationKind.VsCodeTunnelMicrosoft,
            RequiresRenewal = requiresRenewal,
            Requestor = request.Requestor,
            PresentationScope = AccessPresentationScope.Runtime,
            Use = new CredentialUseDescriptor
            {
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                    CredentialUseKind.Completion,
                    lineage,
                    request.Requestor.Identifier,
                    "renew-vscode-tunnel"),
                Kind = CredentialUseKind.Completion,
                Lineage = lineage,
                SafeDisplayName = "Renew VS Code tunnel authentication",
                MemoryScopes = [],
            },
        }, cancellationToken).ConfigureAwait(false);
        if (outcome is not AccessSucceeded<AuthorizationCompletedAccessResult> succeeded)
        {
            return false;
        }

        await succeeded.Result.DisposeAsync().ConfigureAwait(false);
        var status = await this.tunnelStatusResolver.ResolveAsync(request.CliPath, cancellationToken).ConfigureAwait(false);
        return status.Outcome is VsCodeTunnelOutcome.NotRunning or VsCodeTunnelOutcome.Ready or VsCodeTunnelOutcome.RunningDisconnected
            && await this.ProbePresenceAsync(request.CliPath, cancellationToken).ConfigureAwait(false) == VsCodeTunnelOutcome.Ready;
    }

    private Task<VsCodeTunnelOutcome> ProbePresenceAsync(
        string cliPath,
        CancellationToken cancellationToken) => this.cliInvoker.ProbeAsync(
            cliPath,
            ["tunnel", "user", "show"],
            static (exitCode, output, _) => VsCodeTunnelCliAuthorizationExecutor.ReducePresence(exitCode, output),
            cancellationToken);

    private async Task<VsCodeTunnelOutcome> RunHostAsync(
        VsCodeTunnelStartRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var child = this.processLauncher(
            request.CliPath,
            ["tunnel", "--accept-server-license-terms", "--name", request.TunnelName]);
        try
        {
            await this.initialStatusCheckDelayAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.HasExited || (child.Evidence.TokenErrorSeen && !child.Evidence.ConnectedSeen))
                {
                    if (!child.HasExited)
                    {
                        child.Kill();
                    }

                    var completion = await child.WaitForExitAndDrainAsync(cancellationToken).ConfigureAwait(false);
                    var finalStatus = await this.tunnelStatusResolver.ResolveAsync(request.CliPath, cancellationToken).ConfigureAwait(false);
                    return Classify(cancellationToken.IsCancellationRequested, finalStatus, child.Evidence, completion);
                }

                var status = await this.tunnelStatusResolver.ResolveAsync(request.CliPath, cancellationToken).ConfigureAwait(false);
                var outcome = Classify(cancellationToken.IsCancellationRequested, status, child.Evidence, completion: null);
                if (outcome is not (VsCodeTunnelOutcome.Ready or VsCodeTunnelOutcome.RunningDisconnected))
                {
                    child.Kill();
                    var completion = await child.WaitForExitAndDrainAsync(cancellationToken).ConfigureAwait(false);
                    return Classify(cancellationToken.IsCancellationRequested, status, child.Evidence, completion);
                }

                await this.waitBetweenPollsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            child.Kill();
            await child.WaitForExitAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static VsCodeTunnelOutcome Classify(
        bool cancellationRequested,
        VsCodeTunnelResolution status,
        VsCodeTunnelProcessEvidence evidence,
        VsCodeTunnelProcessCompletion? completion)
    {
        if (cancellationRequested || status.Outcome == VsCodeTunnelOutcome.Cancelled)
            return VsCodeTunnelOutcome.Cancelled;
        if (evidence.ConnectedSeen || completion?.ConnectedSeen == true)
            return VsCodeTunnelOutcome.Ready;
        if (evidence.TokenErrorSeen || completion?.TokenErrorSeen == true)
            return VsCodeTunnelOutcome.AuthenticationRequired;
        if (completion?.UnauthorizedHttp401Seen == true)
            return VsCodeTunnelOutcome.AuthenticationRequired;
        if (status.Outcome == VsCodeTunnelOutcome.NotRunning)
            return VsCodeTunnelOutcome.NotRunning;
        if (status.Outcome == VsCodeTunnelOutcome.Ready)
            return VsCodeTunnelOutcome.Ready;
        if (status.Outcome == VsCodeTunnelOutcome.RunningDisconnected)
            return VsCodeTunnelOutcome.RunningDisconnected;
        if (status.Outcome == VsCodeTunnelOutcome.InvalidStatus)
            return VsCodeTunnelOutcome.InvalidStatus;
        return VsCodeTunnelOutcome.CommandFailed;
    }

    private static WorkspaceToolExecutionResult Result(VsCodeTunnelOutcome outcome) =>
        outcome == VsCodeTunnelOutcome.Ready
            ? WorkspaceToolExecutionResult.Success() with { ResultContent = "VS Code tunnel: Ready." }
            : Failure(outcome);

    private static WorkspaceToolExecutionResult Failure(VsCodeTunnelOutcome outcome) =>
        WorkspaceToolExecutionResult.Failure($"VS Code tunnel: {outcome}.");

    private static string? ReadProperty(JsonElement? data, string propertyName) =>
        data is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString() : null;

    internal static ProcessStartInfo BuildHostStartInfo(
        string cliPath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = VsCodeCliLocator.BuildProcessStartInfo(cliPath, arguments);
        startInfo.Environment["VSCODE_CLI_MACHINE_STATUS"] = "1";
        startInfo.Environment.Remove("VSCODE_CLI_ACCESS_TOKEN");
        return startInfo;
    }

    private static IVsCodeTunnelChildProcess LaunchProcess(
        string cliPath,
        IReadOnlyList<string> arguments)
    {
        var process = new Process { StartInfo = BuildHostStartInfo(cliPath, arguments) };
        try
        {
            process.Start();
            return new ProcessBackedVsCodeTunnelChildProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}
