using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Copilot;

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
