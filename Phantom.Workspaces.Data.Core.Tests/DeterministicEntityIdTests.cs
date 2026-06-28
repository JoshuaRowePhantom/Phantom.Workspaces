using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public sealed class DeterministicEntityIdTests
{
    [Fact]
    public void Create_WithSameInputs_ReturnsSameEntityId()
    {
        var first = DeterministicEntityId.Create("git-workspace-scan", "c:/repos/my-project");
        var second = DeterministicEntityId.Create("git-workspace-scan", "c:/repos/my-project");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Create_WithDifferentInputs_ReturnDifferentEntityIds()
    {
        var a = DeterministicEntityId.Create("git-workspace-scan", "c:/repos/project-a");
        var b = DeterministicEntityId.Create("git-workspace-scan", "c:/repos/project-b");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Create_MultipleInputsJoinedWithSlash_EqualsEquivalentSingleInput()
    {
        // Joining with "/" means Create("a","b") has the same canonical string as Create("a/b")
        var multiPart = DeterministicEntityId.Create("a", "b");
        var singlePart = DeterministicEntityId.Create("a/b");

        Assert.Equal(multiPart, singlePart);
    }

    [Fact]
    public void Create_GitWorkspaceScanPath_ProducesKnownGuid()
    {
        // Regression: known canonical → known GUID for the git-workspace-scan tool namespace
        var id = DeterministicEntityId.Create("git-workspace-scan", "c:/repositories/my-project");

        Assert.Equal(new EntityId(Guid.Parse("455928d3-fd7b-2b21-7405-f4240bb1077c")), id);
    }

    [Fact]
    public void Create_EntityNameComponents_ProducesKnownGuid()
    {
        // Regression: known canonical → known GUID for a multi-component entity name
        var id = DeterministicEntityId.Create(
            "computer-user-profiles",
            "users",
            "username",
            "alice",
            "computers",
            "hostname",
            "desktop",
            "copilot",
            "mcp-servers",
            "my-server");

        Assert.Equal(new EntityId(Guid.Parse("4ab99348-7ac3-a464-c841-27442493916a")), id);
    }
}
