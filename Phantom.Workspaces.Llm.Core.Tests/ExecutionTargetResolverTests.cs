using System.Text.Json;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ExecutionTargetResolverTests
{
    [Fact]
    public void ExecutionTargetResolver_NullTrustProfile_ReturnsLocal()
    {
        var resolved = new ExecutionTargetResolver().Resolve(null);

        Assert.Equal("local", resolved.GetProperty("type").GetString());
    }

    [Fact]
    public void ExecutionTargetResolver_AbsentTarget_ReturnsLocal()
    {
        var resolved = new ExecutionTargetResolver().Resolve(new TrustProfile());

        Assert.Equal("local", resolved.GetProperty("type").GetString());
    }

    [Fact]
    public void ExecutionTargetResolver_UserComputerProfile_PassesThrough()
    {
        using var target = JsonDocument.Parse(
            """{"type":"user-computer-profile","entity-id":"11111111-1111-1111-1111-111111111111"}""");
        var trustProfile = new TrustProfile
        {
            DefaultExecutionTarget = target.RootElement.Clone(),
        };

        var resolved = new ExecutionTargetResolver().Resolve(trustProfile);

        Assert.Equal("user-computer-profile", resolved.GetProperty("type").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", resolved.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void ExecutionTargetResolver_RemoteTargetClientInstance_DescriptorContainsEntityIdProperty()
    {
        var resolved = new ExecutionTargetResolver().ResolveDescriptor("remote-a");

        Assert.Equal("user-computer-profile", resolved.GetProperty("type").GetString());
        Assert.Equal("remote-a", resolved.GetProperty("entity-id").GetString());
        Assert.False(resolved.TryGetProperty("target-client-instance", out _));
    }

    [Fact]
    public void ExecutionTargetResolver_LocalTargetClientInstance_DescriptorHasLocalType()
    {
        var resolved = new ExecutionTargetResolver().ResolveDescriptor(TrustProfile.LocalClientInstance);

        Assert.Equal("local", resolved.GetProperty("type").GetString());
        Assert.False(resolved.TryGetProperty("entity-id", out _));
    }
}
