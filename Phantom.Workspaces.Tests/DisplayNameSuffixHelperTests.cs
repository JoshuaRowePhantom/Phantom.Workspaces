using Phantom.Workspaces.Utilities;

namespace Phantom.Workspaces.Tests;

public sealed class DisplayNameSuffixHelperTests
{
    [Fact]
    public void GetNextAvailableName_NoExistingNames_ReturnsBaseName()
    {
        var name = DisplayNameSuffixHelper.GetNextAvailableName("Foo", ["Bar"]);

        Assert.Equal("Foo", name);
    }

    [Fact]
    public void GetNextAvailableName_BaseNameExists_ReturnsSuffix2()
    {
        var name = DisplayNameSuffixHelper.GetNextAvailableName("Foo", ["Foo"]);

        Assert.Equal("Foo (2)", name);
    }

    [Fact]
    public void GetNextAvailableName_Suffix2Exists_ReturnsSuffix3()
    {
        var name = DisplayNameSuffixHelper.GetNextAvailableName("Foo", ["Foo", "Foo (2)"]);

        Assert.Equal("Foo (3)", name);
    }

    [Fact]
    public void GetNextAvailableName_ExistingNameHasSuffix_StripsBeforeComparing()
    {
        var name = DisplayNameSuffixHelper.GetNextAvailableName("Foo (2)", ["Foo", "Foo (2)"]);

        Assert.Equal("Foo (3)", name);
    }
}
