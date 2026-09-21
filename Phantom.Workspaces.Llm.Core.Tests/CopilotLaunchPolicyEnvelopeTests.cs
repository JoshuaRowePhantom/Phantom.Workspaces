using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Copilot;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotLaunchPolicyEnvelopeTests
{
    [Fact]
    public void PolicyEnvelope_OwnerRestrictedFile_RoundTripsAndDeletesOnRead()
    {
        using var directory = new TestDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new CopilotLaunchPolicyStore(directory.Path, time);
        using var lease = store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42);
        if (OperatingSystem.IsWindows())
        {
            AssertRestrictedAcl(directory.Path);
            AssertRestrictedAcl(Path.GetDirectoryName(lease.Path)!);
        }

        var envelope = store.Consume(lease.Path, expectedParentProcessId: 42);

        Assert.Equal(CopilotLaunchPolicyEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
        Assert.Equal(42, envelope.ParentProcessId);
        Assert.Equal(16, Convert.FromHexString(envelope.Nonce).Length);
        Assert.False(File.Exists(lease.Path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(lease.Path)));
    }

    [Fact]
    public void PolicyEnvelope_PreExistingWrongOwner_IsRepairedWhenPermitted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var directory = new TestDirectory();
        var info = Directory.CreateDirectory(directory.Path);
        var security = info.GetAccessControl();
        var current = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        if (Equals(current, administrators))
            return;

        try
        {
            security.SetOwner(administrators);
            info.SetAccessControl(security);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or PrivilegeNotHeldException
                or InvalidOperationException)
        {
            return;
        }

        Assert.Equal(
            administrators,
            info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));

        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System);
        using var lease = store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42);

        AssertRestrictedAcl(directory.Path);
        AssertRestrictedAcl(Path.GetDirectoryName(lease.Path)!);
    }

    [Fact]
    public void PolicyEnvelope_PreExistingWrongOwnerCannotBeRepaired_Rejects()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.Path);
        var security = new RecordingDirectorySecurity(directory.Path, failRoot: true);
        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System, security);

        Assert.Throws<UnauthorizedAccessException>(
            () => store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42));

        Assert.True(security.RootRepairAttempted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void PolicyEnvelope_EnvelopeAclFailure_CleansUpDirectory()
    {
        using var directory = new TestDirectory();
        var security = new RecordingDirectorySecurity(directory.Path, failRoot: false);
        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System, security);

        Assert.Throws<UnauthorizedAccessException>(
            () => store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42));

        Assert.True(Directory.Exists(directory.Path));
        Assert.Equal(2, security.RestrictAttempts);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task PolicyEnvelope_ConcurrentCreation_RestrictsAndConsumesEveryEnvelope()
    {
        using var directory = new TestDirectory();
        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System);
        const int creatorCount = 8;
        using var start = new Barrier(creatorCount + 1);
        var createTasks = Enumerable.Range(0, creatorCount)
            .Select(index => Task.Run(
                () =>
                {
                    start.SignalAndWait();
                    return store.Create(
                        CopilotRuntimeConnectionFactoryTests.CreatePolicy(),
                        100 + index);
                }))
            .ToArray();
        start.SignalAndWait();
        var leases = await Task.WhenAll(createTasks);

        try
        {
            Assert.Equal(creatorCount, leases.Select(lease => lease.Path).Distinct().Count());
            Assert.All(leases, lease => Assert.True(File.Exists(lease.Path)));
            if (OperatingSystem.IsWindows())
                AssertRestrictedAcl(directory.Path);

            foreach (var (lease, index) in leases.Select((lease, index) => (lease, index)))
            {
                if (OperatingSystem.IsWindows())
                    AssertRestrictedAcl(Path.GetDirectoryName(lease.Path)!);

                var envelope = store.Consume(lease.Path, 100 + index);
                Assert.Equal(100 + index, envelope.ParentProcessId);
                Assert.False(File.Exists(lease.Path));
                Assert.False(Directory.Exists(Path.GetDirectoryName(lease.Path)));
            }
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [Fact]
    public async Task PolicyEnvelope_ConcurrentCreation_HoldsCanonicalRootLockOnlyForRootAcl()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var directory = new TestDirectory();
        using var distinctDirectory = new TestDirectory();
        var equivalentRoot = directory.Path + Path.DirectorySeparatorChar;
        Assert.Same(
            CopilotLaunchPolicyStore.GetLaunchRootLock(directory.Path),
            CopilotLaunchPolicyStore.GetLaunchRootLock(equivalentRoot));
        Assert.NotSame(
            CopilotLaunchPolicyStore.GetLaunchRootLock(directory.Path),
            CopilotLaunchPolicyStore.GetLaunchRootLock(distinctDirectory.Path));

        var security = new RootLockAssertingDirectorySecurity(directory.Path);
        var stores = new[]
        {
            new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System, security),
            new CopilotLaunchPolicyStore(equivalentRoot, TimeProvider.System, security),
        };
        using var start = new Barrier(stores.Length + 1);
        var createTasks = stores.Select((store, index) => Task.Run(
            () =>
            {
                start.SignalAndWait();
                return store.Create(
                    CopilotRuntimeConnectionFactoryTests.CreatePolicy(),
                    200 + index);
            })).ToArray();
        start.SignalAndWait();
        var leases = await Task.WhenAll(createTasks);

        try
        {
            Assert.Equal(1, security.RootAttempts);
            Assert.Equal(stores.Length, security.EnvelopeAttempts);
            AssertRestrictedAcl(directory.Path);
            foreach (var lease in leases)
                AssertRestrictedAcl(Path.GetDirectoryName(lease.Path)!);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [Fact]
    public async Task PolicyEnvelope_ConcurrentEnvelopeAclFailure_IsIsolated()
    {
        using var directory = new TestDirectory();
        var security = new FailFirstEnvelopeDirectorySecurity(directory.Path);
        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System, security);
        using var start = new Barrier(3);
        var createTasks = Enumerable.Range(0, 2).Select(index => Task.Run(
            () =>
            {
                start.SignalAndWait();
                try
                {
                    return new CreationResult(
                        index,
                        store.Create(
                            CopilotRuntimeConnectionFactoryTests.CreatePolicy(),
                            300 + index),
                        null);
                }
                catch (Exception exception)
                {
                    return new CreationResult(index, null, exception);
                }
            })).ToArray();
        start.SignalAndWait();
        var results = await Task.WhenAll(createTasks);

        var failure = Assert.Single(results, result => result.Exception is not null);
        Assert.IsType<UnauthorizedAccessException>(failure.Exception);
        var success = Assert.Single(results, result => result.Lease is not null);
        using var lease = success.Lease!;
        Assert.Single(Directory.EnumerateDirectories(directory.Path));
        if (OperatingSystem.IsWindows())
        {
            AssertRestrictedAcl(directory.Path);
            AssertRestrictedAcl(Path.GetDirectoryName(lease.Path)!);
        }

        var envelope = store.Consume(lease.Path, 300 + success.Index);

        Assert.Equal(300 + success.Index, envelope.ParentProcessId);
        Assert.False(File.Exists(lease.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task PolicyEnvelope_DirectoryReplacedDuringAclApplication_RejectsAndPreservesTarget()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var directory = new TestDirectory();
        var parent = Path.GetDirectoryName(directory.Path)!;
        var target = Directory.CreateDirectory(Path.Combine(parent, $"policy-target-{Guid.NewGuid():N}"));
        var marker = Path.Combine(target.FullName, "marker.txt");
        File.WriteAllText(marker, "preserve");
        var stagedJunction = Path.Combine(parent, $"policy-junction-{Guid.NewGuid():N}");
        await CreateJunctionAsync(stagedJunction, target.FullName);
        var security = new ReplacingDirectorySecurity(directory.Path, stagedJunction);
        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System, security);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42));

            Assert.True(File.Exists(marker));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        }
        finally
        {
            if (Directory.Exists(stagedJunction))
                Directory.Delete(stagedJunction);
            Directory.Delete(target.FullName, recursive: true);
        }
    }

    [Fact]
    public void PolicyEnvelope_ExpiredOrOversized_RejectsBeforeLaunch()
    {
        using var directory = new TestDirectory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new CopilotLaunchPolicyStore(directory.Path, time);
        using var lease = store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42);
        time.Advance(TimeSpan.FromMinutes(6));

        Assert.Throws<InvalidDataException>(
            () => store.Consume(lease.Path, expectedParentProcessId: 42));

        var oversizedDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, Guid.NewGuid().ToString("N")));
        var oversized = Path.Combine(oversizedDirectory.FullName, "policy.json");
        File.WriteAllBytes(oversized, new byte[CopilotLaunchPolicyStore.MaximumEnvelopeBytes + 1]);
        Assert.Throws<InvalidDataException>(
            () => store.Consume(oversized, expectedParentProcessId: 42));

        var emptyDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, Guid.NewGuid().ToString("N")));
        var empty = Path.Combine(emptyDirectory.FullName, "policy.json");
        File.WriteAllBytes(empty, []);
        Assert.Throws<InvalidDataException>(
            () => store.Consume(empty, expectedParentProcessId: 42));
        Assert.False(File.Exists(empty));
    }

    [Fact]
    public void PolicyEnvelope_UnknownMemberOrWrongParent_Rejects()
    {
        using var directory = new TestDirectory();
        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System);
        using var lease = store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42);
        var json = File.ReadAllText(lease.Path).TrimEnd('}', '\r', '\n') + ",\"unexpected\":true}";
        File.WriteAllText(lease.Path, json);

        Assert.Throws<InvalidDataException>(
            () => store.Consume(lease.Path, expectedParentProcessId: 42));

        using var otherLease = store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42);
        Assert.Throws<InvalidDataException>(
            () => store.Consume(otherLease.Path, expectedParentProcessId: 43));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("future")]
    [InlineData("lifetime")]
    [InlineData("nonce-null")]
    [InlineData("nonce-length")]
    [InlineData("nonce-hex")]
    [InlineData("policy-null")]
    public void PolicyEnvelope_InvalidSemanticField_Rejects(string mutation)
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new CopilotLaunchPolicyStore(directory.Path, new FakeTimeProvider(now));
        using var lease = store.Create(CopilotRuntimeConnectionFactoryTests.CreatePolicy(), 42);
        var envelope = JsonNode.Parse(File.ReadAllText(lease.Path))!.AsObject();
        switch (mutation)
        {
            case "schema":
                envelope["schemaVersion"] = 2;
                break;
            case "future":
                envelope["createdUtc"] = now.AddMinutes(1);
                break;
            case "lifetime":
                envelope["expiresUtc"] = now.AddMinutes(6);
                break;
            case "nonce-null":
                envelope["nonce"] = null;
                break;
            case "nonce-length":
                envelope["nonce"] = "00";
                break;
            case "nonce-hex":
                envelope["nonce"] = new string('Z', 32);
                break;
            case "policy-null":
                envelope["policy"] = null;
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation '{mutation}'.");
        }
        File.WriteAllText(lease.Path, envelope.ToJsonString());

        Assert.Throws<InvalidDataException>(
            () => store.Consume(lease.Path, expectedParentProcessId: 42));
    }

    [Fact]
    public void PolicyEnvelope_OutsideMissingOrDirectoryPath_Rejects()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var directory = new TestDirectory();
        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System);
        var outside = Path.Combine(
            Path.GetDirectoryName(directory.Path)!,
            $"outside-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(outside, "{}");
        try
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => store.Consume(outside, Environment.ProcessId));
            Assert.Throws<UnauthorizedAccessException>(
                () => store.Consume(
                    Path.Combine(directory.Path, "missing", "policy.json"),
                    Environment.ProcessId));

            var directoryPath = Directory.CreateDirectory(
                Path.Combine(directory.Path, "policy.json")).FullName;
            Assert.Throws<UnauthorizedAccessException>(
                () => store.Consume(directoryPath, Environment.ProcessId));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task PolicyEnvelope_IntermediateAncestorReparsePoint_Rejects()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var directory = new TestDirectory();
        var target = Directory.CreateDirectory(
            Path.Combine(Path.GetDirectoryName(directory.Path)!, $"policy-target-{Guid.NewGuid():N}"));
        var child = Directory.CreateDirectory(Path.Combine(target.FullName, "child"));
        var targetPath = Path.Combine(child.FullName, "policy.json");
        File.WriteAllText(targetPath, "{}");
        Directory.CreateDirectory(directory.Path);
        var link = Path.Combine(directory.Path, "linked");
        await CreateJunctionAsync(link, target.FullName);
        var store = new CopilotLaunchPolicyStore(directory.Path, TimeProvider.System);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => store.Consume(
                    Path.Combine(link, "child", "policy.json"),
                    Environment.ProcessId));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task PolicyEnvelope_LaunchRootReparsePoint_Rejects()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var directory = new TestDirectory();
        var target = Directory.CreateDirectory(
            Path.Combine(Path.GetDirectoryName(directory.Path)!, $"policy-root-{Guid.NewGuid():N}"));
        var envelopeDirectory = Directory.CreateDirectory(
            Path.Combine(target.FullName, Guid.NewGuid().ToString("N")));
        var targetPath = Path.Combine(envelopeDirectory.FullName, "policy.json");
        File.WriteAllText(targetPath, "{}");
        var link = directory.Path;
        await CreateJunctionAsync(link, target.FullName);
        var store = new CopilotLaunchPolicyStore(link, TimeProvider.System);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => store.Consume(
                    Path.Combine(link, envelopeDirectory.Name, "policy.json"),
                    Environment.ProcessId));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target.FullName, recursive: true);
        }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
        }) ?? throw new InvalidOperationException("Failed to start junction creation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Junction creation failed: {await output} {await error}");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertRestrictedAcl(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var current = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.Equal(current, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        Assert.True(security.AreAccessRulesCanonical);
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
            Assert.True(
                Equals(rule.IdentityReference, current)
                    || Equals(rule.IdentityReference, system));
        });
        Assert.Contains(rules, rule => Equals(rule.IdentityReference, current));
        Assert.Contains(rules, rule => Equals(rule.IdentityReference, system));
        Assert.True(CopilotLaunchPolicyStore.HasRestrictedAcl(path));
    }

    private sealed class RecordingDirectorySecurity(
        string root,
        bool failRoot) : ICopilotLaunchPolicyDirectorySecurity
    {
        public int RestrictAttempts { get; private set; }
        public bool RootRepairAttempted { get; private set; }

        public void RestrictDirectory(string path)
        {
            RestrictAttempts++;
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                RootRepairAttempted = true;
                if (failRoot)
                    throw new UnauthorizedAccessException("The wrong owner could not be repaired.");
                return;
            }

            throw new UnauthorizedAccessException("The envelope ACL could not be applied.");
        }
    }

    private sealed class ReplacingDirectorySecurity(
        string root,
        string stagedJunction) : ICopilotLaunchPolicyDirectorySecurity
    {
        public void RestrictDirectory(string path)
        {
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.Delete(path);
            Directory.Move(stagedJunction, path);
        }
    }

    private sealed class RootLockAssertingDirectorySecurity(string root)
        : ICopilotLaunchPolicyDirectorySecurity
    {
        private readonly string canonicalRoot = Canonicalize(root);
        private readonly object rootLock = CopilotLaunchPolicyStore.GetLaunchRootLock(root);
        private readonly ICopilotLaunchPolicyDirectorySecurity inner =
            new CopilotLaunchPolicyStore.CopilotLaunchPolicyDirectorySecurity();
        private int rootAttempts;
        private int envelopeAttempts;

        public int RootAttempts => Volatile.Read(ref this.rootAttempts);
        public int EnvelopeAttempts => Volatile.Read(ref this.envelopeAttempts);

        public void RestrictDirectory(string path)
        {
            if (string.Equals(
                Canonicalize(path),
                this.canonicalRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(Monitor.IsEntered(this.rootLock));
                Interlocked.Increment(ref this.rootAttempts);
            }
            else
            {
                Assert.False(Monitor.IsEntered(this.rootLock));
                Interlocked.Increment(ref this.envelopeAttempts);
            }

            this.inner.RestrictDirectory(path);
        }
    }

    private sealed class FailFirstEnvelopeDirectorySecurity(string root)
        : ICopilotLaunchPolicyDirectorySecurity
    {
        private readonly string canonicalRoot = Canonicalize(root);
        private readonly ICopilotLaunchPolicyDirectorySecurity inner =
            new CopilotLaunchPolicyStore.CopilotLaunchPolicyDirectorySecurity();
        private int envelopeAttempts;

        public void RestrictDirectory(string path)
        {
            if (!string.Equals(
                    Canonicalize(path),
                    this.canonicalRoot,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)
                && Interlocked.Increment(ref this.envelopeAttempts) == 1)
            {
                throw new UnauthorizedAccessException("The envelope ACL could not be applied.");
            }

            this.inner.RestrictDirectory(path);
        }
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed record CreationResult(
        int Index,
        CopilotLaunchPolicyLease? Lease,
        Exception? Exception);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                Environment.CurrentDirectory,
                "TestResults",
                $"copilot-policy-{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
