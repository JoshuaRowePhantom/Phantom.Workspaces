using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

internal sealed class DevTunnelConnectManagementApi
{
    private readonly Func<AuthenticationHeaderValue, ITunnelManagementClient> createClient;

    internal DevTunnelConnectManagementApi(Func<AuthenticationHeaderValue, ITunnelManagementClient>? createClient = null)
    {
        this.createClient = createClient ?? (header => new TunnelManagementClient(
            new ProductInfoHeaderValue("Phantom.Workspaces", "1.0"),
            () => Task.FromResult<AuthenticationHeaderValue?>(header),
            ManagementApiVersions.Version20230927Preview));
    }

    internal async ValueTask<DevTunnelConnectManagementOutcome> MintAsync(
        DevTunnelAccessDescriptor descriptor,
        DevTunnelIdentityScheme scheme,
        string identity,
        CancellationToken cancellationToken)
    {
        var client = this.createClient(new AuthenticationHeaderValue(
            scheme switch
            {
                DevTunnelIdentityScheme.GitHub => TunnelAuthenticationSchemes.GitHub,
                DevTunnelIdentityScheme.Entra => TunnelAuthenticationSchemes.Aad,
                _ => "Bearer",
            }, identity));
        try
        {
            var candidates = await client.ListTunnelsAsync(null, null, new TunnelRequestOptions
            {
                IncludePorts = true,
                Labels = [DevTunnelNaming.WorkspacesMarkerLabel],
                RequireAllLabels = true,
            }, true, cancellationToken).ConfigureAwait(false);
            var matches = (candidates ?? []).Where(tunnel =>
                tunnel.Labels?.Contains(DevTunnelNaming.WorkspacesMarkerLabel, StringComparer.Ordinal) == true &&
                (DevTunnelNaming.IsAuto(descriptor.Name) ||
                    tunnel.Labels.Contains(descriptor.Name!, StringComparer.Ordinal)) &&
                (descriptor.Endpoint is null ||
                    (TryEndpoint(tunnel, out var candidateEndpoint) &&
                        SameEndpoint(descriptor.Endpoint, candidateEndpoint)))).ToArray();
            if (matches.Length != 1 || !TryEndpoint(matches[0], out var endpoint) ||
                (descriptor.Endpoint is { } configured && !SameEndpoint(configured, endpoint)))
            {
                return new DevTunnelConnectManagementFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
            }

            var selected = matches[0];
            var minted = await client.GetTunnelAsync(selected, new TunnelRequestOptions
            {
                IncludePorts = true,
                TokenScopes = [TunnelAccessScopes.Connect],
            }, cancellationToken).ConfigureAwait(false);
            if (minted is null || minted.TunnelId != selected.TunnelId || minted.ClusterId != selected.ClusterId ||
                !TryEndpoint(minted, out var mintedEndpoint) || !SameEndpoint(endpoint, mintedEndpoint) ||
                minted.AccessTokens?.TryGetValue(TunnelAccessScopes.Connect, out var token) != true ||
                string.IsNullOrEmpty(token) || !TryExpiry(token, out var expiresOn))
            {
                return new DevTunnelConnectManagementFailed { ErrorCode = AccessErrorCode.ProviderFailure };
            }
            return new DevTunnelConnectManagementSucceeded
            {
                Material = token.ToCharArray(),
                Endpoint = endpoint,
                ExpiresOn = expiresOn,
            };
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new DevTunnelConnectManagementUnauthorized();
        }
        catch (UnauthorizedAccessException exception) when (
            exception.InnerException is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
        {
            return new DevTunnelConnectManagementUnauthorized();
        }
        catch (UnauthorizedAccessException)
        {
            return new DevTunnelConnectManagementFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
        catch (InvalidOperationException exception) when (exception.InnerException is HttpRequestException)
        {
            return new DevTunnelConnectManagementFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
        catch (HttpRequestException)
        {
            return new DevTunnelConnectManagementFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
        finally
        {
            if (client is IAsyncDisposable asynchronous)
            {
                await asynchronous.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                (client as IDisposable)?.Dispose();
            }
        }
    }

    private static bool SameEndpoint(Uri left, Uri right) =>
        Uri.Compare(left, right, UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool TryEndpoint(Tunnel tunnel, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(tunnel.TunnelId) || string.IsNullOrWhiteSpace(tunnel.ClusterId) ||
            tunnel.Ports is not { Length: 1 } ports ||
            !Uri.TryCreate($"https://{tunnel.TunnelId}-{ports[0].PortNumber}.{tunnel.ClusterId}.devtunnels.ms/",
                UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps || !candidate.IsDefaultPort ||
            candidate.UserInfo.Length != 0 || candidate.Query.Length != 0 || candidate.Fragment.Length != 0 ||
            candidate.AbsolutePath != "/" ||
            !candidate.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        endpoint = candidate;
        return true;
    }

    private static bool TryExpiry(string token, out DateTimeOffset expiresOn)
    {
        expiresOn = default;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length > 32768)
        {
            return false;
        }
        byte[]? payload = null;
        try
        {
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            payload = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("exp", out var expiration) ||
                !expiration.TryGetInt64(out var seconds))
            {
                return false;
            }
            expiresOn = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }
}

internal abstract record DevTunnelConnectManagementOutcome;
internal sealed record DevTunnelConnectManagementUnauthorized : DevTunnelConnectManagementOutcome;
internal sealed record DevTunnelConnectManagementFailed : DevTunnelConnectManagementOutcome
{
    public required AccessErrorCode ErrorCode { get; init; }
}
internal sealed record DevTunnelConnectManagementSucceeded : DevTunnelConnectManagementOutcome, IDisposable
{
    public required char[] Material { get; init; }
    public required Uri Endpoint { get; init; }
    public required DateTimeOffset ExpiresOn { get; init; }

    public void Dispose() => CryptographicOperations.ZeroMemory(
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(this.Material.AsSpan()));
}
