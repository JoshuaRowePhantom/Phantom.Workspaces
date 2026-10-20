using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.Access;

public sealed class AccessHostServices : IAsyncDisposable
{
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposalStarted;

    private AccessHostServices() { }

    public required IAccessBroker Broker { get; init; }
    public required IUserAuthorizationRegistry AuthorizationRegistry { get; init; }
    public required AuthorizationRequestRowsViewModel AuthorizationRows { get; init; }
    public required IReleaseMemoryAdministration ReleaseMemoryAdministration { get; init; }

    public static AccessHostServices Create(AccessHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.PlatformSecretStore);
        ArgumentNullException.ThrowIfNull(options.Dispatch);
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(options.ReleaseMemoryFilePath));
        var registry = new UserAuthorizationRegistry();
        var coordinator = new ProviderAcquisitionCoordinator();
        var executors = new CredentialAccessExecutorRegistry();
        var storedSecret = new StoredSecretAccessExecutor(options.PlatformSecretStore);
        var gitHub = new GitHubCredentialAccessExecutor();
        var entra = new EntraCredentialAccessExecutor();
        executors.Add<StoredSecretAccessRequest, StoredSecretAccessResult>(storedSecret);
        executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(gitHub);
        executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(entra);
        executors.Add<DevTunnelIdentityCredentialAccessRequest, DevTunnelIdentityCredentialAccessResult>(
            new DevTunnelIdentityCredentialAccessExecutor(gitHub, entra));
        executors.AddCompletion<CliOwnedAuthorizationRequest, AuthorizationCompletedAccessResult>(
            new VsCodeTunnelCliAuthorizationExecutor());
        var broker = new AccessBroker(policy, executors, coordinator, registry);
        try
        {
            var rows = new AuthorizationRequestRowsViewModel { Dispatch = options.Dispatch, Registry = registry };
            rows.SetPresentationMode(AuthorizationRowsPresentationMode.Bootstrap);
            return new AccessHostServices
            {
                Broker = broker,
                AuthorizationRegistry = registry,
                AuthorizationRows = rows,
                ReleaseMemoryAdministration = new ReleaseMemoryAdministration { Policy = policy },
            };
        }
        catch (Exception constructionFailure)
        {
            try
            {
                broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Access host construction and cleanup failed.", constructionFailure, cleanupFailure);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposalStarted, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        var failures = new List<Exception>();
        try
        {
            this.AuthorizationRows.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await this.Broker.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            var failure = new AggregateException("Access host shutdown did not complete cleanly.", failures);
            this.disposal.TrySetException(failure);
            throw failure;
        }

        this.disposal.TrySetResult();
    }
}
