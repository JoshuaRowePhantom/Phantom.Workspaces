using System;
using System.Collections.Generic;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Phantom-owned subclass of <see cref="OAuthConnection"/> that carries the host-pinned Entra
/// <c>authority</c> field (issue #1420). <see cref="OAuthConnection"/> (Microsoft-owned AgentSchema)
/// silently drops unknown properties like <c>authority</c> on load — exactly as it drops the Phantom
/// <c>type</c> field on <see cref="McpTool"/> (issue #1416). Phantom therefore resolves the field
/// itself and upgrades each <see cref="OAuthConnection"/> whose <see cref="Connection.AuthenticationMode"/>
/// is <c>entra-pinned</c> to a <see cref="PhantomOAuthConnection"/> via <see cref="PhantomAgentSchema"/>.
/// </summary>
/// <remarks>
/// The <c>authenticationMode</c> discriminator itself already round-trips natively (AgentSchema's
/// <see cref="Connection"/> base declares <see cref="Connection.AuthenticationMode"/>), so it is
/// inherited here rather than redeclared. Only <see cref="Authority"/> is new.
/// </remarks>
public sealed class PhantomOAuthConnection : OAuthConnection
{
    /// <summary>
    /// The Microsoft Entra tenant authority (e.g. <c>https://login.microsoftonline.com/&lt;tenant&gt;/v2.0</c>)
    /// used to acquire tokens in <c>entra-pinned</c> mode. Null when not configured.
    /// </summary>
    public string? Authority { get; set; }

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

        return result;
    }
}
