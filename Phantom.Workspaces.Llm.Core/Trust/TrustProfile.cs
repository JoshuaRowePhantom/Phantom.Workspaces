using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>Container network access policy.</summary>
public enum TrustNetworkAccessPolicy
{
    /// <summary>No network access.</summary>
    NoNetwork = 0,

    /// <summary>Local network access only.</summary>
    LocalNetwork = 1,

    /// <summary>NAT'd network access.</summary>
    NattedNetwork = 2,

    /// <summary>Host network access.</summary>
    HostNetwork = 3,
}

/// <summary>Mount access mode.</summary>
public enum TrustMountAccessMode
{
    /// <summary>Read-only access.</summary>
    ReadOnly = 0,

    /// <summary>Read-write access.</summary>
    ReadWrite = 1,
}

/// <summary>Docker mount type.</summary>
public enum TrustMountType
{
    /// <summary>Host bind mount.</summary>
    Bind = 0,

    /// <summary>Named volume.</summary>
    Volume = 1,

    /// <summary>In-memory scratch mount.</summary>
    Tmpfs = 2,
}

/// <summary>HTTPS proxy policy mode.</summary>
public enum TrustHttpsProxyMode
{
    /// <summary>Never use a proxy.</summary>
    Disabled = 0,

    /// <summary>Proxy if reachable.</summary>
    Optional = 1,

    /// <summary>Must proxy.</summary>
    Required = 2,
}

/// <summary>
/// How a base trust profile combines with the profile that inherits it.
/// </summary>
public enum TrustInheritanceMode
{
    /// <summary>The base narrows the inheriting profile (intersection / most-restrictive).</summary>
    Restrictive = 0,

    /// <summary>The base widens the inheriting profile (union / most-permissive).</summary>
    Permissive = 1,
}

/// <summary>A reference to a base trust profile plus the mode used to combine it.</summary>
public sealed record TrustProfileBaseReference(
    string ProfileName,
    TrustInheritanceMode Mode = TrustInheritanceMode.Restrictive);

/// <summary>A single mount declaration granted by a trust profile.</summary>
public sealed record TrustMountPoint(
    string SourcePath,
    string TargetPath,
    TrustMountAccessMode AccessMode,
    TrustMountType Type);

/// <summary>HTTPS proxy egress policy.</summary>
public sealed record TrustHttpsProxyPolicy(
    TrustHttpsProxyMode Mode,
    string? ProxyUrl = null,
    string? CredentialsReference = null)
{
    /// <summary>The default policy: proxy disabled.</summary>
    public static TrustHttpsProxyPolicy Disabled { get; } = new(TrustHttpsProxyMode.Disabled);
}

/// <summary>
/// The user-semantic (entity-level) trust profile, parsed from a persisted
/// <c>llm-trust-profile</c> entity prior to composition.
/// </summary>
public sealed record TrustProfileDefinition
{
    /// <summary>Client instances this profile may run on; <c>"."</c> denotes the local instance.</summary>
    public IReadOnlyList<string> HostingWorkspacesClientInstances { get; init; } = [];

    /// <summary>Container mount points granted by this profile.</summary>
    public IReadOnlyList<TrustMountPoint> MountPoints { get; init; } = [];

    /// <summary>Connection descriptor used as this profile's default execution target.</summary>
    public JsonElement? DefaultExecutionTarget { get; init; }

    /// <summary>Container network access policy.</summary>
    public TrustNetworkAccessPolicy NetworkAccessPolicy { get; init; } = TrustNetworkAccessPolicy.NoNetwork;

    /// <summary>HTTPS proxy policy.</summary>
    public TrustHttpsProxyPolicy HttpsProxyPolicy { get; init; } = TrustHttpsProxyPolicy.Disabled;

    /// <summary>One or more MCP tool-call envelope schemas; composed with <c>anyOf</c>.</summary>
    public IReadOnlyList<JsonObject> AllowedMcpToolCallSchemas { get; init; } = [];

    /// <summary>
    /// One or more MCP tool-call envelope schemas that are explicitly denied. A tool call matching
    /// any restricted schema is rejected even if it also matches an allowed schema. Composed
    /// independently of <see cref="AllowedMcpToolCallSchemas"/>.
    /// </summary>
    public IReadOnlyList<JsonObject> RestrictedMcpToolCallSchemas { get; init; } = [];
}

/// <summary>
/// The runtime/composed trust profile. User semantics (names, base references) are stripped;
/// only the effective execution policy remains.
/// </summary>
public sealed record TrustProfile
{
    /// <summary>Identifier for the local client instance.</summary>
    public const string LocalClientInstance = ".";

    /// <summary>Wildcard identifier permitting execution on any client instance ("all machines").</summary>
    public const string WildcardClientInstance = "*";

    /// <summary>Effective set of client instances this profile may run on.</summary>
    public IReadOnlyList<string> HostingWorkspacesClientInstances { get; init; } = [];

    /// <summary>Effective container mount points.</summary>
    public IReadOnlyList<TrustMountPoint> MountPoints { get; init; } = [];

    /// <summary>Effective default execution target connection descriptor.</summary>
    public JsonElement? DefaultExecutionTarget { get; init; }

    /// <summary>Effective container network access policy.</summary>
    public TrustNetworkAccessPolicy NetworkAccessPolicy { get; init; } = TrustNetworkAccessPolicy.NoNetwork;

    /// <summary>Effective HTTPS proxy policy.</summary>
    public TrustHttpsProxyPolicy HttpsProxyPolicy { get; init; } = TrustHttpsProxyPolicy.Disabled;

    /// <summary>Effective MCP tool-call schema, composed as a single <c>anyOf</c> envelope.</summary>
    public JsonObject AllowedMcpToolCallSchema { get; init; } = new();

    /// <summary>Whether this profile permits execution on the given client instance.</summary>
    public bool AllowsClientInstance(string clientInstance)
    {
        ArgumentNullException.ThrowIfNull(clientInstance);
        foreach (var instance in this.HostingWorkspacesClientInstances)
        {
            if (string.Equals(instance, WildcardClientInstance, StringComparison.Ordinal)
                || string.Equals(instance, clientInstance, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this profile permits execution on the local client instance.</summary>
    public bool AllowsLocalExecution() => this.AllowsClientInstance(LocalClientInstance);
}
