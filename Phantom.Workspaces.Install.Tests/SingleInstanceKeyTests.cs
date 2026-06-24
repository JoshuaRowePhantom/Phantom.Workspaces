using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class SingleInstanceKeyTests
{
    [Fact]
    public void Compute_IsDeterministicForTheSameConfigPath()
    {
        var first = SingleInstanceKey.Compute(@"C:\configs\a.json");
        var second = SingleInstanceKey.Compute(@"C:\configs\a.json");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_DiffersForDifferentConfigPaths()
    {
        var a = SingleInstanceKey.Compute(@"C:\configs\a.json");
        var b = SingleInstanceKey.Compute(@"C:\configs\b.json");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_IsCaseInsensitiveAndPathNormalized()
    {
        var lower = SingleInstanceKey.Compute(@"C:\Configs\A.json");
        var mixed = SingleInstanceKey.Compute(@"C:\configs\sub\..\a.JSON");
        Assert.Equal(lower, mixed);
    }

    [Fact]
    public void Compute_FallsBackToDefaultBasisWhenNoConfig()
    {
        var fromNull = SingleInstanceKey.Compute(null);
        var fromEmpty = SingleInstanceKey.Compute("");
        var fromWhitespace = SingleInstanceKey.Compute("   ");
        Assert.Equal(fromNull, fromEmpty);
        Assert.Equal(fromNull, fromWhitespace);
    }

    [Fact]
    public void Compute_PrefersExplicitInstanceKey()
    {
        var explicitKey = SingleInstanceKey.Compute(@"C:\configs\a.json", "instance-2");
        var fromConfig = SingleInstanceKey.Compute(@"C:\configs\a.json");
        Assert.NotEqual(fromConfig, explicitKey);
        Assert.Equal(SingleInstanceKey.Compute(null, "instance-2"), explicitKey);
    }

    [Fact]
    public void MutexAndPipeNames_CarryTheirPrefixes()
    {
        Assert.StartsWith(SingleInstanceKey.MutexPrefix, SingleInstanceKey.MutexName(@"C:\a.json"));
        Assert.StartsWith(SingleInstanceKey.PipePrefix, SingleInstanceKey.PipeName(@"C:\a.json"));
    }
}
