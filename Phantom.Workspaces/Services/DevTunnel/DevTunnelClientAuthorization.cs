using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

public static class DevTunnelClientAuthorization
{
    public static void ValidateEndpoint(Uri? destination, DevTunnelConnectCredentialAccessResult credential) =>
        ValidateEndpoint(destination, credential.Endpoint);

    internal static void ValidateEndpoint(Uri? destination, Uri endpoint)
    {
        if (destination is not { IsAbsoluteUri: true } ||
            Uri.Compare(destination, endpoint, UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0 ||
            destination.Scheme != Uri.UriSchemeHttps || destination.UserInfo.Length != 0 ||
            !destination.IsDefaultPort)
        {
            throw new DevTunnelCredentialAccessException(AccessErrorCode.IdentityMismatch);
        }
    }
}
