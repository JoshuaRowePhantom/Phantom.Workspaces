using System.Reflection;
using Xunit;

namespace Phantom.Workspaces.Gui.Shared.Tests;

/// <summary>
/// Guard for issue #1101: this headless test assembly must disable test parallelization so the
/// stock Avalonia.Headless.XUnit single-thread dispatch loop is never driven concurrently.
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
}
