using System.Reflection;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

/// <summary>
/// Guard for issue #1101: this headless test assembly must disable test parallelization so the stock
/// Avalonia.Headless.XUnit single-thread dispatch loop is never driven concurrently, and it must prefer
/// PerTest isolation (the previous PerAssembly override was removed so failures cannot cascade).
/// </summary>
public sealed class HeadlessCollectionBehaviorGuardTests
{
    [Fact]
    public void HeadlessTestProject_DeclaresNonParallelCollectionBehavior()
    {
        var assembly = typeof(HeadlessCollectionBehaviorGuardTests).Assembly;
        var attribute = assembly.GetCustomAttribute<CollectionBehaviorAttribute>();

        Assert.NotNull(attribute);
        Assert.True(
            attribute!.DisableTestParallelization,
            $"{assembly.GetName().Name} must declare [assembly: CollectionBehavior(DisableTestParallelization = true)].");
        Assert.Equal(1, attribute.MaxParallelThreads);
    }

    [Fact]
    public void HeadlessTestProject_DoesNotDeclarePerAssemblyIsolation()
    {
        var assembly = typeof(HeadlessCollectionBehaviorGuardTests).Assembly;
        var isolation = assembly.GetCustomAttribute<AvaloniaTestIsolationAttribute>();

        if (isolation is not null)
        {
            Assert.NotEqual(AvaloniaTestIsolationLevel.PerAssembly, isolation.IsolationLevel);
        }
    }
}
