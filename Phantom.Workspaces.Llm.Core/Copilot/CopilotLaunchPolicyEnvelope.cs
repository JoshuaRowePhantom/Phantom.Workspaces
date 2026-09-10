using Phantom.Workspaces.Llm.Processes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Copilot;

internal interface ICopilotLaunchPolicyStore
{
    CopilotLaunchPolicyLease Create(MxcProcessPolicy policy, int? parentProcessId = null);
}

/// <summary>A short-lived, one-use handoff for a locally compiled Copilot launch policy.</summary>
public sealed record CopilotLaunchPolicyEnvelope(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    int ParentProcessId,
    string Nonce,
    MxcProcessPolicy Policy)
{
    /// <summary>The current handoff schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Owns an unconsumed launch-policy file.</summary>
public sealed class CopilotLaunchPolicyLease : IDisposable, IAsyncDisposable
{
    private string? path;

    internal CopilotLaunchPolicyLease(string path) => this.path = path;

    /// <summary>The absolute policy-file path.</summary>
    public string Path => this.path ?? throw new ObjectDisposedException(nameof(CopilotLaunchPolicyLease));

    /// <inheritdoc/>
    public void Dispose()
    {
        var ownedPath = Interlocked.Exchange(ref this.path, null);
        if (ownedPath is null)
            return;

        try
        {
            File.Delete(ownedPath);
            var directory = System.IO.Path.GetDirectoryName(ownedPath);
            if (directory is not null)
                Directory.Delete(directory, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Creates and consumes secured, bounded, one-use Copilot policy envelopes.</summary>
public sealed class CopilotLaunchPolicyStore : ICopilotLaunchPolicyStore
{
    /// <summary>Maximum serialized envelope size.</summary>
    public const int MaximumEnvelopeBytes = 1024 * 1024;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string launchRoot;
    private readonly TimeProvider timeProvider;

    /// <summary>Create a store rooted in the per-user local launch directory.</summary>
    public CopilotLaunchPolicyStore()
        : this(GetDefaultLaunchRoot(), TimeProvider.System)
    {
    }

    /// <summary>Create a store with explicit paths and time source.</summary>
    public CopilotLaunchPolicyStore(string launchRoot, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.launchRoot = System.IO.Path.GetFullPath(launchRoot);
        this.timeProvider = timeProvider;
    }

    /// <summary>The canonical default root used by both the host and wrapper.</summary>
    public static string GetDefaultLaunchRoot() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Phantom.Workspaces",
            "CopilotLaunch");

    /// <summary>Create a one-use envelope owned by the current process.</summary>
    public CopilotLaunchPolicyLease Create(
        MxcProcessPolicy policy,
        int? parentProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        CleanupExpiredFiles();

        Directory.CreateDirectory(this.launchRoot);
        RestrictDirectory(this.launchRoot);
        var directory = System.IO.Path.Combine(this.launchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);

        var now = this.timeProvider.GetUtcNow();
        var envelope = new CopilotLaunchPolicyEnvelope(
            CopilotLaunchPolicyEnvelope.CurrentSchemaVersion,
            now,
            now.Add(MaximumLifetime),
            parentProcessId ?? Environment.ProcessId,
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            policy);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length > MaximumEnvelopeBytes)
            throw new InvalidOperationException("The compiled Copilot launch policy exceeds 1 MiB.");

        var path = System.IO.Path.Combine(directory, "policy.json");
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return new CopilotLaunchPolicyLease(path);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    /// <summary>Validate, deserialize, and delete an envelope before process launch.</summary>
    public CopilotLaunchPolicyEnvelope Consume(string path, int expectedParentProcessId)
    {
        var canonicalPath = ValidateContainedNormalFile(path);
        CopilotLaunchPolicyEnvelope? envelope;
        try
        {
            using var stream = new FileStream(
                canonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumEnvelopeBytes)
                throw new InvalidDataException("The Copilot launch policy has an invalid size.");
            try
            {
                envelope = JsonSerializer.Deserialize<CopilotLaunchPolicyEnvelope>(stream, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The Copilot launch policy is invalid.", exception);
            }
        }
        finally
        {
            TryDeleteParentDirectory(canonicalPath);
        }

        var now = this.timeProvider.GetUtcNow();
        if (envelope is null
            || envelope.SchemaVersion != CopilotLaunchPolicyEnvelope.CurrentSchemaVersion
            || envelope.ParentProcessId != expectedParentProcessId
            || envelope.CreatedUtc > now
            || envelope.ExpiresUtc <= now
            || envelope.ExpiresUtc - envelope.CreatedUtc > MaximumLifetime
            || envelope.Nonce is null
            || envelope.Nonce.Length != 32
            || !IsHex(envelope.Nonce)
            || envelope.Policy is null)
        {
            throw new InvalidDataException("The Copilot launch policy envelope is invalid or expired.");
        }

        return envelope;
    }

    /// <summary>Checks that a directory grants access only to the current user and SYSTEM.</summary>
    public static bool HasRestrictedAcl(string directory)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        return HasRestrictedWindowsAcl(directory);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasRestrictedWindowsAcl(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl();
        if (!security.AreAccessRulesProtected)
            return false;
        var current = WindowsIdentity.GetCurrent().User;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToArray();
        return Equals(security.GetOwner(typeof(SecurityIdentifier)), current)
            && rules.Length > 0
            && rules.All(rule =>
                Equals(rule.IdentityReference, current)
                || Equals(rule.IdentityReference, system));
    }

    private string ValidateContainedNormalFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = System.IO.Path.GetFullPath(path);
        var relative = System.IO.Path.GetRelativePath(this.launchRoot, canonicalPath);
        if (System.IO.Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The policy path is outside the Copilot launch directory.");
        }
        if (!File.Exists(canonicalPath)
            || (File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The policy path is not a normal file.");
        }
        var containingDirectory = System.IO.Path.GetDirectoryName(canonicalPath)!;
        if ((File.GetAttributes(this.launchRoot) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(containingDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The policy path traverses a reparse point.");
        }
        return canonicalPath;
    }

    private void CleanupExpiredFiles()
    {
        if (!Directory.Exists(this.launchRoot))
            return;

        var cutoff = this.timeProvider.GetUtcNow().Subtract(MaximumLifetime);
        foreach (var directory in Directory.EnumerateDirectories(this.launchRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff.UtcDateTime)
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var current = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            current,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    private static void TryDeleteParentDirectory(string path)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (directory is not null)
                Directory.Delete(directory, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
