using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Install;

/// <summary>The persisted health-gate state recording an applied-but-unconfirmed version.</summary>
public sealed record HealthState
{
    /// <summary>The version <c>current</c> was just repointed to but which has not yet booted OK.</summary>
    [JsonPropertyName("pendingVersion")]
    public string? PendingVersion { get; init; }

    /// <summary>The version to roll back to if the pending version never reaches "ready".</summary>
    [JsonPropertyName("rollbackVersion")]
    public string? RollbackVersion { get; init; }
}

/// <summary>
/// The launcher/health gate that enables automatic rollback. After an apply repoints
/// <c>current</c> to a new version, the gate records it as <em>pending</em> with the previous
/// version as the rollback target. The new version clears the pending mark once it reaches
/// "ready". If a later launch/apply finds the pending version is still current but was never
/// confirmed, it repoints <c>current</c> back to the retained previous version.
/// </summary>
public sealed class HealthGate
{
    /// <summary>The health marker file name under the managed root.</summary>
    public const string HealthMarkerFileName = ".update-health.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly IFileSystem fileSystem;
    private readonly InstallLayout layout;

    /// <summary>Creates the gate over <paramref name="fileSystem"/> and <paramref name="layout"/>.</summary>
    public HealthGate(IFileSystem fileSystem, InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        this.fileSystem = fileSystem;
        this.layout = layout;
    }

    /// <summary>The health marker path under the managed root.</summary>
    public string HealthMarkerPath => Path.Combine(this.layout.AppRoot, HealthMarkerFileName);

    /// <summary>Records that <paramref name="newVersion"/> was applied, retaining <paramref name="rollbackVersion"/>.</summary>
    public void MarkApplied(string newVersion, string? rollbackVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        this.Write(new HealthState { PendingVersion = newVersion, RollbackVersion = rollbackVersion });
    }

    /// <summary>Clears the pending mark when <paramref name="version"/> reaches "ready".</summary>
    public void ConfirmHealthy(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var state = this.Read();
        if (state?.PendingVersion is { } pending
            && string.Equals(pending, version, StringComparison.OrdinalIgnoreCase))
        {
            this.Clear();
        }
    }

    /// <summary>The current health state, or <c>null</c> when none is recorded.</summary>
    public HealthState? Read()
    {
        if (!this.fileSystem.FileExists(this.HealthMarkerPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<HealthState>(
            this.fileSystem.ReadAllText(this.HealthMarkerPath), SerializerOptions);
    }

    /// <summary>
    /// Rolls <c>current</c> back to the retained previous version when the pending version is still
    /// current but was never confirmed healthy. Returns <c>true</c> when a rollback occurred.
    /// </summary>
    public bool EvaluateAndRollback()
    {
        var state = this.Read();
        if (state?.PendingVersion is not { } pending)
        {
            return false;
        }

        var current = this.layout.ResolveCurrentVersion();
        if (!string.Equals(current, pending, StringComparison.OrdinalIgnoreCase))
        {
            // The pending version is no longer current; nothing to roll back.
            this.Clear();
            return false;
        }

        if (state.RollbackVersion is { } rollback
            && this.fileSystem.DirectoryExists(this.layout.GetVersionDirectory(rollback)))
        {
            this.layout.RepointCurrent(rollback);
            this.Clear();
            return true;
        }

        this.Clear();
        return false;
    }

    private void Write(HealthState state)
    {
        this.fileSystem.CreateDirectory(this.layout.AppRoot);
        this.fileSystem.WriteAllText(this.HealthMarkerPath, JsonSerializer.Serialize(state, SerializerOptions));
    }

    private void Clear() => this.fileSystem.DeleteFile(this.HealthMarkerPath);
}
