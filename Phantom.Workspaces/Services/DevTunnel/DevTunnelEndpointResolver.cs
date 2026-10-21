using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

public sealed class DevTunnelEndpointResolver(
    IAccessBroker broker,
    DevTunnelConnectCredentialAccessRequest request,
    Func<AccessPresentationScope> presentationScope) : IDevTunnelEndpointResolver
{
    private readonly Func<AccessPresentationScope> getPresentationScope =
        presentationScope ?? throw new ArgumentNullException(nameof(presentationScope));

    public async Task<DevTunnelEndpointResolution> ResolveAsync(
        string tunnelName,
        DevTunnelAccessMode accessMode,
        CancellationToken cancellationToken = default)
    {
        var access = accessMode == DevTunnelAccessMode.Anonymous ? DevTunnelAccessKind.Anonymous : DevTunnelAccessKind.Private;
        if (!string.Equals(request.Tunnel.Name, tunnelName, StringComparison.Ordinal) || access != request.Tunnel.Access)
        {
            throw new DevTunnelCredentialAccessException(AccessErrorCode.IdentityMismatch);
        }
        await using var result = await DevTunnelCredentialAccessException.RequireAsync(
            await broker.AcquireAsync(request with { PresentationScope = this.getPresentationScope() },
                cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return new DevTunnelEndpointResolution(result.Endpoint, result.CreateGenerationInvalidation());
    }
}
