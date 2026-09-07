using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>Filesystem path access mode.</summary>
public enum TrustFilesystemAccessMode
{
    /// <summary>Read-only access.</summary>
    ReadOnly = 0,

    /// <summary>Read-write access.</summary>
    ReadWrite = 1,
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

/// <summary>A host filesystem path granted by a trust profile.</summary>
public sealed record TrustFilesystemPath(
    string SourcePath,
    string? TargetPath,
    TrustFilesystemAccessMode AccessMode)
{
    /// <summary>The target path used when applying this grant.</summary>
    public string EffectiveTargetPath => this.TargetPath ?? this.SourcePath;
}

/// <summary>Controls access to persistent Copilot configuration and session state.</summary>
public abstract record TrustDataSharing
{
    /// <summary>Shared persistent data.</summary>
    public static TrustDataSharing Full { get; } = new FullSharing();

    /// <summary>No persistent data.</summary>
    public static TrustDataSharing None { get; } = new NoSharing();

    /// <summary>Persistent data isolated to a named regime.</summary>
    public static TrustDataSharing Regime(string regimeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regimeName);
        return new RegimeScoped(regimeName);
    }

    /// <summary>Shared persistent data.</summary>
    public sealed record FullSharing : TrustDataSharing;

    /// <summary>Persistent data isolated to a named regime.</summary>
    public sealed record RegimeScoped(string RegimeName) : TrustDataSharing;

    /// <summary>No persistent data.</summary>
    public sealed record NoSharing : TrustDataSharing;
}

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

    /// <summary>Host filesystem paths granted by this profile.</summary>
    public IReadOnlyList<TrustFilesystemPath> FilesystemPaths { get; init; } = [];

    /// <summary>Connection descriptor used as this profile's default execution target.</summary>
    public JsonElement? DefaultExecutionTarget { get; init; }

    /// <summary>AppContainer capabilities, or <see langword="null"/> when networking is unconstrained.</summary>
    public IReadOnlyList<string>? NetworkCapabilities { get; init; }

    /// <summary>Copilot persistent-data sharing policy.</summary>
    public TrustDataSharing DataSharing { get; init; } = TrustDataSharing.Full;

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

    /// <summary>Effective host filesystem path grants.</summary>
    public IReadOnlyList<TrustFilesystemPath> FilesystemPaths { get; init; } = [];

    /// <summary>Effective default execution target connection descriptor.</summary>
    public JsonElement? DefaultExecutionTarget { get; init; }

    /// <summary>Effective AppContainer capabilities, or <see langword="null"/> when unconstrained.</summary>
    public IReadOnlyList<string>? NetworkCapabilities { get; init; }

    /// <summary>Effective Copilot persistent-data sharing policy.</summary>
    public TrustDataSharing DataSharing { get; init; } = TrustDataSharing.Full;

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
