using System;
using System.Collections.Generic;
using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Phantom-owned subclass of <see cref="OAuthConnection"/> that carries provider authority and
/// explicit trusted account endpoint/claim configuration. <see cref="OAuthConnection"/> (Microsoft-owned AgentSchema)
/// silently drops unknown properties like <c>authority</c> on load — exactly as it drops the Phantom
/// <c>type</c> field on <see cref="McpTool"/> (issue #1416). Phantom therefore resolves the field
/// itself and upgrades each <see cref="OAuthConnection"/> whose <see cref="Connection.AuthenticationMode"/>
/// is <c>entra-pinned</c> to a <see cref="PhantomOAuthConnection"/> via <see cref="PhantomAgentSchema"/>.
/// </summary>
/// <remarks>
/// The <c>authenticationMode</c> discriminator itself already round-trips natively (AgentSchema's
/// <see cref="Connection"/> base declares <see cref="Connection.AuthenticationMode"/>), so it is
/// inherited here rather than redeclared.
/// </remarks>
public sealed class PhantomOAuthConnection : OAuthConnection
{
    /// <summary>
    /// The explicitly configured provider authority/issuer, also used by host-pinned Entra.
    /// Null when not configured.
    /// </summary>
    public string? Authority { get; set; }
    /// <summary>Configured HTTPS account evidence endpoint, never a fabricated userinfo URL.</summary>
    public string? AccountEndpoint { get; set; }
    /// <summary>The explicitly configured stable claim returned by the account evidence endpoint.</summary>
    public OAuthAccountIdentifierClaim? AccountIdentifierClaim { get; set; }
    /// <summary>The explicitly configured credential audience, independent of the provider endpoints.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Builds a <see cref="PhantomOAuthConnection"/> from a plain <see cref="OAuthConnection"/>,
    /// copying every base field and attaching <paramref name="authority"/>.
    /// </summary>
    public static PhantomOAuthConnection From(OAuthConnection source, string? authority)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new PhantomOAuthConnection
        {
            Kind = source.Kind,
            Endpoint = source.Endpoint,
            ClientId = source.ClientId,
            ClientSecret = source.ClientSecret,
            TokenUrl = source.TokenUrl,
            Scopes = source.Scopes,
            AuthenticationMode = source.AuthenticationMode,
            UsageDescription = source.UsageDescription,
            Authority = authority,
            AccountEndpoint = (source as PhantomOAuthConnection)?.AccountEndpoint,
            AccountIdentifierClaim = (source as PhantomOAuthConnection)?.AccountIdentifierClaim,
            Audience = (source as PhantomOAuthConnection)?.Audience,
        };
    }

    /// <summary>
    /// Re-emits the Phantom <c>authority</c> field so <c>ToJson()</c> → <c>FromJson()</c> round-trips
    /// preserve it (and the <c>authenticationMode</c>) instead of silently dropping <c>authority</c>
    /// (issue #1420, mirrors <see cref="PhantomMcpTool.Save"/>).
    /// </summary>
    public override Dictionary<string, object?> Save(SaveContext? context = null)
    {
        var result = base.Save(context!);
        if (!string.IsNullOrEmpty(this.Authority))
        {
            result["authority"] = this.Authority;
        }
        if (!string.IsNullOrEmpty(this.AccountEndpoint))
        {
            result["accountEndpoint"] = this.AccountEndpoint;
        }
        if (this.AccountIdentifierClaim is { } claim)
        {
            result["accountIdentifierClaim"] = claim.ToString();
        }
        if (!string.IsNullOrEmpty(this.Audience))
        {
            result["audience"] = this.Audience;
        }

        return result;
    }
}
