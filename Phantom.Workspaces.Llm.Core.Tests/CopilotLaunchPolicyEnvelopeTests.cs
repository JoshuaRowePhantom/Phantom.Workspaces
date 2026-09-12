using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Copilot;
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
            Assert.True(CopilotLaunchPolicyStore.HasRestrictedAcl(Path.GetDirectoryName(lease.Path)!));
        }

        var envelope = store.Consume(lease.Path, expectedParentProcessId: 42);

        Assert.Equal(CopilotLaunchPolicyEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
        Assert.Equal(42, envelope.ParentProcessId);
        Assert.Equal(16, Convert.FromHexString(envelope.Nonce).Length);
        Assert.False(File.Exists(lease.Path));
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
