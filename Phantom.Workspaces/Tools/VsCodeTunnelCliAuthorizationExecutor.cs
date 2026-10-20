using System.ComponentModel;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Tools;

internal sealed class VsCodeTunnelCliAuthorizationExecutor :
    ICompletionAuthorizationExecutor<CliOwnedAuthorizationRequest, AuthorizationCompletedAccessResult>,
    IAsyncDisposable
{
    private readonly VsCodeCliInvoker invoker;
    private readonly IVsCodeTunnelStatusResolver statusResolver;
    private readonly VsCodeTunnelInteractiveLoginLauncher launcher;
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();

    public VsCodeTunnelCliAuthorizationExecutor(
        VsCodeCliInvoker? invoker = null,
        IVsCodeTunnelStatusResolver? statusResolver = null,
        VsCodeTunnelInteractiveLoginLauncher? launcher = null)
    {
        this.invoker = invoker ?? new VsCodeCliInvoker();
        this.statusResolver = statusResolver ?? new VsCodeTunnelStatusResolver(this.invoker);
        this.launcher = launcher ?? new VsCodeTunnelInteractiveLoginLauncher();
    }

    public CredentialExecutorKind Kind => CredentialExecutorKind.CliOwned;

    public async ValueTask<CompletionAuthorizationInspection> InspectAsync(
        CliOwnedAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        var status = await this.statusResolver.ResolveAsync(request.CliPath, cancellationToken).ConfigureAwait(false);
        var presence = await this.ProbePresenceAsync(request.CliPath, cancellationToken).ConfigureAwait(false);
        return new CompletionAuthorizationInspection
        {
            IsCompleted = !request.RequiresRenewal
                && presence == VsCodeTunnelOutcome.Ready
                && status.Outcome is VsCodeTunnelOutcome.NotRunning or VsCodeTunnelOutcome.Ready or VsCodeTunnelOutcome.RunningDisconnected,
            Source = new CredentialSourceChoiceDescriptor
            {
                Identifier = CreateKey(request).Source,
                Provider = CredentialProviderKind.CliOwned,
                Kind = CredentialSourceKind.CliOwned,
                Usability = CredentialSourceUsability.AcquisitionRequired,
                SafeDisplayName = request.Kind switch
                {
                    CliOwnedAuthorizationKind.VsCodeTunnelGitHub => "VS Code tunnel — GitHub",
                    CliOwnedAuthorizationKind.VsCodeTunnelMicrosoft => "VS Code tunnel — Microsoft",
                    _ => throw new ArgumentOutOfRangeException(nameof(request)),
                },
            },
            AcquisitionKey = CreateKey(request),
        };
    }

    public ValueTask<AuthorizationCompletedAccessResult> CreateCompletedResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AuthorizationCompletedAccessResult());
    }

    internal async Task<VsCodeTunnelOutcome> ProbePresenceAsync(
        string cliPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await this.invoker.ProbeAsync(
                cliPath,
                ["tunnel", "user", "show"],
                static (exitCode, output, _) => ReducePresence(exitCode, output),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException or InvalidOperationException)
        {
            return VsCodeTunnelOutcome.CommandFailed;
        }
    }

    internal static VsCodeTunnelOutcome ReducePresence(
        int exitCode,
        string output) => (exitCode, output.Trim()) switch
    {
        (0, "logged in with provider github" or "logged in with provider microsoft") => VsCodeTunnelOutcome.Ready,
        (1, "not logged in") => VsCodeTunnelOutcome.AuthenticationRequired,
        _ => VsCodeTunnelOutcome.CommandFailed,
    };

    public async ValueTask<CredentialExecutionOutcome<AuthorizationCompletedAccessResult>> ExecuteAsync(
        CliOwnedAuthorizationRequest request,
        ProviderInteractionPermit permit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        var status = await this.statusResolver.ResolveAsync(request.CliPath, cancellation.Token).ConfigureAwait(false);
        var presence = await this.ProbePresenceAsync(request.CliPath, cancellation.Token).ConfigureAwait(false);
        if (presence == VsCodeTunnelOutcome.CommandFailed
            || status.Outcome is VsCodeTunnelOutcome.CommandFailed or VsCodeTunnelOutcome.InvalidStatus or VsCodeTunnelOutcome.Cancelled)
        {
            return new CredentialExecutionFailed<AuthorizationCompletedAccessResult>
            {
                ErrorCode = AccessErrorCode.ProviderFailure,
                CanRetry = true,
            };
        }

        var key = CreateKey(request);
        var operation = new LoginOperation
        {
            Owner = this,
            Request = request,
            CompatibilityKey = key,
            RequestIdentifier = context.RequestIdentifier,
            Revision = context.Revision,
            AcquisitionGeneration = context.AcquisitionGeneration,
        };
        var outcome = await context.ExecuteProviderAcquisitionAsync(operation, permit, cancellation.Token).ConfigureAwait(false);
        return outcome switch
        {
            ProviderAcquisitionSucceeded => new CredentialExecutionReady<AuthorizationCompletedAccessResult>
            {
                Materializer = new CompletionMaterializer(),
            },
            ProviderAcquisitionDenied => new CredentialExecutionDenied<AuthorizationCompletedAccessResult>(),
            ProviderAcquisitionCancelled => throw new OperationCanceledException(cancellation.Token),
            ProviderAcquisitionShuttingDown => throw new OperationCanceledException(cancellation.Token),
            _ => new CredentialExecutionFailed<AuthorizationCompletedAccessResult>
            {
                ErrorCode = AccessErrorCode.ProviderFailure,
                CanRetry = true,
            },
        };
    }

    internal static ProviderAcquisitionKey CreateKey(CliOwnedAuthorizationRequest request)
    {
        var provider = request.Kind switch
        {
            CliOwnedAuthorizationKind.VsCodeTunnelGitHub => "github",
            CliOwnedAuthorizationKind.VsCodeTunnelMicrosoft => "microsoft",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        var partition = OperatingSystem.IsWindows() ? request.CliPath.ToUpperInvariant() : request.CliPath;
        return new ProviderAcquisitionKey
        {
            Configuration = CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
            {
                Provider = CredentialProviderKind.CliOwned,
                Kind = CredentialKind.Completion,
                CredentialPartition = partition,
                SourceCompatibility = provider,
                Authority = provider,
                Audience = "vscode-tunnel",
                ClientPartition = partition,
                Scopes = [],
            }),
            Source = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.CliOwned, partition + ":" + provider),
            Protocol = ProviderProtocolKind.Cli,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync().ConfigureAwait(false);
        await this.loginGate.WaitAsync().ConfigureAwait(false);
        this.loginGate.Release();
        this.lifetime.Dispose();
    }

    private sealed class LoginOperation : IProviderAcquisitionOperation
    {
        public required VsCodeTunnelCliAuthorizationExecutor Owner { get; init; }
        public required CliOwnedAuthorizationRequest Request { get; init; }
        public required ProviderAcquisitionKey CompatibilityKey { get; init; }
        public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
        public required long Revision { get; init; }
        public required Guid AcquisitionGeneration { get; init; }
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.CliOwned;

        public async ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken)
        {
            await this.Owner.loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Task<int>? login = null;
                var committed = await commitContext.CommitAsync(
                    new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() },
                    token =>
                    {
                        login = this.Owner.launcher.LaunchAsync(
                            this.Request.CliPath,
                            this.Request.Kind == CliOwnedAuthorizationKind.VsCodeTunnelGitHub
                                ? VsCodeTunnelProvider.GitHub : VsCodeTunnelProvider.Microsoft,
                            commitContext.InteractionPermit,
                            this.CompatibilityKey,
                            token);
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
                if (committed is not ProviderAcquisitionSucceeded || login is null)
                {
                    return committed;
                }

                // Only the CLI owns its cache. Commit authorizes the visible process start;
                // publication waits for process exit and fresh local presence/status guards.
                var exitCode = await login.ConfigureAwait(false);
                if (exitCode != 0
                    || await this.Owner.ProbePresenceAsync(this.Request.CliPath, cancellationToken).ConfigureAwait(false) != VsCodeTunnelOutcome.Ready)
                {
                    throw new LoginFailedException();
                }

                var status = await this.Owner.statusResolver.ResolveAsync(this.Request.CliPath, cancellationToken).ConfigureAwait(false);
                if (status.Outcome is VsCodeTunnelOutcome.CommandFailed or VsCodeTunnelOutcome.InvalidStatus or VsCodeTunnelOutcome.Cancelled)
                {
                    throw new LoginFailedException();
                }

                return committed;
            }
            catch (Exception exception) when (exception is LoginFailedException or Win32Exception or TimeoutException or InvalidOperationException)
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
            }
            finally
            {
                this.Owner.loginGate.Release();
            }
        }
    }

    private sealed class LoginFailedException : Exception;

    private sealed class CompletionMaterializer : IAccessResultMaterializer<AuthorizationCompletedAccessResult>
    {
        public ValueTask<AuthorizationCompletedAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AuthorizationCompletedAccessResult());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
