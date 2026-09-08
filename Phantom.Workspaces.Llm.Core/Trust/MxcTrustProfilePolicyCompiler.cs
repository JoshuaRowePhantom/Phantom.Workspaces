using Microsoft.Mxc.Sdk;
using Phantom.Workspaces.Llm.Processes;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>Severity of a trust-profile policy compilation diagnostic.</summary>
public enum TrustProfilePolicyDiagnosticSeverity
{
    /// <summary>Informational detail about an automatically added restriction or grant.</summary>
    Information,

    /// <summary>A security or compatibility warning that does not prevent containment.</summary>
    Warning,

    /// <summary>A failure that prevents a policy from being emitted.</summary>
    Error,
}

/// <summary>A structured, serializable policy compilation diagnostic.</summary>
public sealed record TrustProfilePolicyDiagnostic(
    string Code,
    TrustProfilePolicyDiagnosticSeverity Severity,
    string Message,
    string? Operation = null,
    string? NativeCode = null,
    string? Remediation = null);

/// <summary>The deterministic result of compiling one effective trust profile.</summary>
public sealed record TrustProfileProcessPolicyCompilation(
    bool RequiresContainment,
    MxcProcessPolicy? Policy,
    IReadOnlyList<TrustProfilePolicyDiagnostic> Diagnostics);

/// <summary>Compiles effective trust profiles into portable MXC process policies.</summary>
public interface ITrustProfileProcessPolicyCompiler
{
    /// <summary>Compile an already composed effective profile without launching a process.</summary>
    TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile);
}

/// <summary>Default fail-closed compiler for Windows ProcessContainer policy.</summary>
public sealed class MxcTrustProfilePolicyCompiler : ITrustProfileProcessPolicyCompiler
{
    private readonly IMxcPolicyHost host;
    private readonly string sessionNonce;

    /// <summary>Create a compiler that probes and canonicalizes on the current launch host.</summary>
    public MxcTrustProfilePolicyCompiler()
        : this(DefaultMxcPolicyHost.Instance)
    {
    }

    internal MxcTrustProfilePolicyCompiler(IMxcPolicyHost host)
    {
        this.host = host;
        sessionNonce = host.SessionNonce;
    }

    /// <inheritdoc/>
    public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
    {
        ArgumentNullException.ThrowIfNull(effectiveProfile);

        var requiresContainment =
            effectiveProfile.FilesystemPaths.Count > 0
            || effectiveProfile.NetworkCapabilities is not null
            || effectiveProfile.DataSharing is not TrustDataSharing.FullSharing;
        if (!requiresContainment)
            return new(false, null, []);

        var diagnostics = new List<TrustProfilePolicyDiagnostic>();
        var readonlyPaths = new List<string>();
        var readwritePaths = new List<string>();
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        foreach (var grant in effectiveProfile.FilesystemPaths)
        {
            var source = CanonicalizePath(grant.SourcePath, "source", diagnostics);
            var target = CanonicalizePath(grant.EffectiveTargetPath, "target", diagnostics);
            if (source is null || target is null)
                continue;
            if (!pathComparer.Equals(source, target))
            {
                diagnostics.Add(Error(
                    "filesystem.remappingUnsupported",
                    $"MXC ProcessContainer cannot remap source '{source}' to target '{target}'."));
                continue;
            }

            AddPath(
                grant.AccessMode == TrustFilesystemAccessMode.ReadWrite
                    ? readwritePaths
                    : readonlyPaths,
                source,
                pathComparer);
        }

        var capabilities = new List<string>();
        foreach (var capability in effectiveProfile.NetworkCapabilities ?? [])
        {
            if (!IsSupportedCapability(capability))
            {
                diagnostics.Add(Error(
                    "network.unsupportedCapability",
                    $"MXC does not support the network capability '{capability}'."));
                continue;
            }
            if (!capabilities.Contains(capability, StringComparer.Ordinal))
                capabilities.Add(capability);
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDataSharingPolicy(
            effectiveProfile.DataSharing,
            sessionNonce,
            readonlyPaths,
            readwritePaths,
            environment,
            diagnostics,
            pathComparer);

        var runtimePath = CanonicalizeHostPath(
            host.CopilotRuntimeDirectory,
            "Copilot runtime",
            diagnostics);
        if (runtimePath is not null)
        {
            AddPath(readonlyPaths, runtimePath, pathComparer);
            diagnostics.Add(new(
                "filesystem.bootstrapGrant",
                TrustProfilePolicyDiagnosticSeverity.Information,
                $"Added the trusted Copilot runtime bootstrap grant '{runtimePath}'."));
        }

        ProbeHost(diagnostics);
        if (diagnostics.Any(item => item.Severity == TrustProfilePolicyDiagnosticSeverity.Error))
            return new(true, null, diagnostics);

        foreach (var readwritePath in readwritePaths)
            readonlyPaths.RemoveAll(path => pathComparer.Equals(path, readwritePath));

        diagnostics.Add(new(
            "mxc.previewSecurity",
            TrustProfilePolicyDiagnosticSeverity.Warning,
            "MXC ProcessContainer support is preview security functionality; review launch warnings."));

        var policy = new MxcProcessPolicy(
            MxcProcessPolicy.CurrentSchemaVersion,
            readonlyPaths.AsReadOnly(),
            readwritePaths.AsReadOnly(),
            capabilities.AsReadOnly(),
            new Dictionary<string, string>(environment, environment.Comparer),
            new MxcProcessContainment(
                MxcContainmentBackend.ProcessContainer,
                LeastPrivilege: true,
                LearningMode: false,
                PermissiveMode: false));
        return new(true, policy, diagnostics.AsReadOnly());
    }

    private void AddDataSharingPolicy(
        TrustDataSharing dataSharing,
        string nonce,
        List<string> readonlyPaths,
        List<string> readwritePaths,
        Dictionary<string, string> environment,
        List<TrustProfilePolicyDiagnostic> diagnostics,
        StringComparer pathComparer)
    {
        var copilotBase = CanonicalizeHostPath(
            host.CopilotBaseDirectory,
            "Copilot configuration",
            diagnostics);
        var tempBase = CanonicalizeHostPath(
            host.TemporaryDirectory,
            "session temporary",
            diagnostics);
        if (copilotBase is null || tempBase is null)
            return;

        string configPath;
        string sessionTempPath;
        switch (dataSharing)
        {
            case TrustDataSharing.FullSharing:
                configPath = copilotBase;
                sessionTempPath = Path.Combine(tempBase, $"copilot-session-{nonce}");
                break;
            case TrustDataSharing.RegimeScoped regime when IsValidScopeName(regime.RegimeName):
                configPath = Path.Combine(copilotBase, $"{regime.RegimeName}-session");
                sessionTempPath = Path.Combine(
                    tempBase,
                    $"copilot-{regime.RegimeName}-{nonce}");
                environment["COPILOT_CONFIG_HOME"] = configPath;
                break;
            case TrustDataSharing.RegimeScoped regime:
                diagnostics.Add(Error(
                    "dataSharing.invalidRegime",
                    $"The regime name '{regime.RegimeName}' is not safe for a host directory."));
                return;
            case TrustDataSharing.NoSharing:
                configPath = Path.Combine(tempBase, $"copilot-ephemeral-{nonce}");
                sessionTempPath = Path.Combine(tempBase, $"copilot-session-{nonce}");
                environment["COPILOT_CONFIG_HOME"] = configPath;
                break;
            default:
                diagnostics.Add(Error(
                    "dataSharing.unsupportedMode",
                    $"The data-sharing mode '{dataSharing.GetType().Name}' is unsupported."));
                return;
        }

        AddPath(readwritePaths, configPath, pathComparer);
        AddPath(readwritePaths, sessionTempPath, pathComparer);
        diagnostics.Add(new(
            "filesystem.dataSharingGrant",
            TrustProfilePolicyDiagnosticSeverity.Information,
            $"Added scoped Copilot configuration grant '{configPath}'."));
        diagnostics.Add(new(
            "filesystem.sessionTempGrant",
            TrustProfilePolicyDiagnosticSeverity.Information,
            $"Added scoped session temporary grant '{sessionTempPath}'."));
    }

    private void ProbeHost(List<TrustProfilePolicyDiagnostic> diagnostics)
    {
        try
        {
            var support = host.GetPlatformSupport();
            if (!support.IsSupported
                || !support.AvailableMethods.Contains(ContainmentBackend.ProcessContainer))
            {
                diagnostics.Add(Error(
                    "host.unsupported",
                    support.Reason
                        ?? "MXC ProcessContainer is unavailable on this launch host."));
                return;
            }

            var backend = host.GetAvailableBackends()
                .FirstOrDefault(item => item.Backend == ContainmentBackend.ProcessContainer);
            if (backend is null)
            {
                diagnostics.Add(Error(
                    "host.backendUnavailable",
                    "MXC reported host support but no ProcessContainer backend is available."));
                return;
            }

            if (backend.Tier == IsolationTier.AppContainerDacl)
            {
                diagnostics.Add(new(
                    "host.daclFallback",
                    TrustProfilePolicyDiagnosticSeverity.Warning,
                    "MXC may use the AppContainer DACL fallback and restore modified ACLs after exit."));
            }
        }
        catch (MxcException exception)
        {
            diagnostics.Add(new(
                "host.probeFailed",
                TrustProfilePolicyDiagnosticSeverity.Error,
                exception.Message,
                exception.Operation,
                exception.NativeCode,
                exception.Remediation));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or DllNotFoundException
                or BadImageFormatException
                or EntryPointNotFoundException
                or JsonException)
        {
            diagnostics.Add(Error(
                "host.probeFailed",
                $"MXC host probing failed: {exception.Message}"));
        }
    }

    private static string? CanonicalizePath(
        string path,
        string role,
        List<TrustProfilePolicyDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || IsRemoteOrDevicePath(path)
            || HasAlternateDataStream(path)
            || HasAmbiguousWindowsSegment(path))
        {
            diagnostics.Add(Error(
                "filesystem.invalidPath",
                $"The {role} path '{path}' must be a non-empty absolute host path."));
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            diagnostics.Add(Error(
                "filesystem.invalidPath",
                $"The {role} path '{path}' is invalid: {exception.Message}"));
            return null;
        }
    }

    private static string? CanonicalizeHostPath(
        string path,
        string purpose,
        List<TrustProfilePolicyDiagnostic> diagnostics)
    {
        var result = CanonicalizePath(path, purpose, diagnostics);
        return result;
    }

    private static bool IsValidScopeName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSupportedCapability(string capability) =>
        !string.IsNullOrWhiteSpace(capability)
        && !capability.Contains(',')
        && !capability.Any(char.IsControl)
        && !capability.Any(char.IsWhiteSpace)
        && !string.Equals(
            capability,
            "learningModeLogging",
            StringComparison.OrdinalIgnoreCase)
        && !string.Equals(
            capability,
            "permissiveLearningMode",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsRemoteOrDevicePath(string path) =>
        OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool HasAlternateDataStream(string path)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        return path.IndexOf(':', rootLength) >= 0;
    }

    private static bool HasAmbiguousWindowsSegment(string path)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        foreach (var segment in path[rootLength..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            {
                return true;
            }

            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4
                    && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                        || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && stem[3] is >= '1' and <= '9'))
            {
                return true;
            }
        }
        return false;
    }

    private static void AddPath(
        List<string> paths,
        string path,
        StringComparer comparer)
    {
        if (!paths.Contains(path, comparer))
            paths.Add(path);
    }

    private static TrustProfilePolicyDiagnostic Error(string code, string message) =>
        new(code, TrustProfilePolicyDiagnosticSeverity.Error, message);
}

internal interface IMxcPolicyHost
{
    string CopilotBaseDirectory { get; }
    string TemporaryDirectory { get; }
    string CopilotRuntimeDirectory { get; }
    string SessionNonce { get; }
    PlatformSupport GetPlatformSupport();
    IReadOnlyList<AvailableBackend> GetAvailableBackends();
}

internal sealed class DefaultMxcPolicyHost : IMxcPolicyHost
{
    public static DefaultMxcPolicyHost Instance { get; } = new();

    public string CopilotBaseDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot");

    public string TemporaryDirectory => Path.GetTempPath();
    public string CopilotRuntimeDirectory => AppContext.BaseDirectory;
    public string SessionNonce => Guid.NewGuid().ToString("N");
    public PlatformSupport GetPlatformSupport() => MxcSandbox.GetPlatformSupport();
    public IReadOnlyList<AvailableBackend> GetAvailableBackends() =>
        MxcSandbox.GetAvailableBackends();
}
