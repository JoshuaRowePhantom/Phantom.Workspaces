using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Processes;

/// <summary>The containment backend selected by a portable MXC process policy.</summary>
public enum MxcContainmentBackend
{
    /// <summary>Windows ProcessContainer containment.</summary>
    ProcessContainer,
}

/// <summary>Portable, fail-closed containment settings for a child process.</summary>
public sealed record MxcProcessContainment(
    MxcContainmentBackend Backend,
    bool LeastPrivilege,
    bool LearningMode,
    bool PermissiveMode);

/// <summary>
/// Versioned local execution contract produced from a trust profile and consumed by a process
/// launcher. The contract contains no user-authored JSON or live SDK objects.
/// </summary>
public sealed record MxcProcessPolicy
{
    /// <summary>The portable contract version shared by local launcher boundaries.</summary>
    public const string CurrentSchemaVersion = "1";

    /// <summary>Create an immutable policy by defensively copying every supplied collection.</summary>
    [JsonConstructor]
    public MxcProcessPolicy(
        string schemaVersion,
        IReadOnlyList<string> readonlyPaths,
        IReadOnlyList<string> readwritePaths,
        IReadOnlyList<string> networkCapabilities,
        IReadOnlyDictionary<string, string> environmentOverrides,
        MxcProcessContainment containment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        ArgumentNullException.ThrowIfNull(readonlyPaths);
        ArgumentNullException.ThrowIfNull(readwritePaths);
        ArgumentNullException.ThrowIfNull(networkCapabilities);
        ArgumentNullException.ThrowIfNull(environmentOverrides);
        ArgumentNullException.ThrowIfNull(containment);

        SchemaVersion = schemaVersion;
        ReadonlyPaths = Array.AsReadOnly(readonlyPaths.ToArray());
        ReadwritePaths = Array.AsReadOnly(readwritePaths.ToArray());
        NetworkCapabilities = Array.AsReadOnly(networkCapabilities.ToArray());
        EnvironmentOverrides = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environmentOverrides, StringComparer.OrdinalIgnoreCase));
        Containment = containment;
    }

    /// <summary>Portable contract schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Canonical host paths granted read-only access.</summary>
    public IReadOnlyList<string> ReadonlyPaths { get; }

    /// <summary>Canonical host paths granted read-write access.</summary>
    public IReadOnlyList<string> ReadwritePaths { get; }

    /// <summary>Validated ProcessContainer capability names.</summary>
    public IReadOnlyList<string> NetworkCapabilities { get; }

    /// <summary>Environment values that the launcher must apply to the child.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentOverrides { get; }

    /// <summary>Explicit fail-closed containment settings.</summary>
    public MxcProcessContainment Containment { get; }
}
