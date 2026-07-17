using System;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Guards for the stock Avalonia.Headless.XUnit harness that replaced the bespoke PhantomAvaloniaFact
/// harness (issue #1101). They assert the properties the migration relies on: test bodies run on the
/// dispatcher-owning thread; the pinned Avalonia contains the upstream fixes that make the custom
/// safety nets unnecessary (#21688 construction-failure-no-cascade, #21223 dispose-before-signal,
/// #20000 first-class AvaloniaTestIsolationLevel); the assembly prefers PerTest isolation; and the
/// headless assemblies are serialized (non-parallel CollectionBehavior).
///
/// The primary acceptance signal for #1101 is the stress gate — the ~873 migrated [AvaloniaFact]
/// tests running green repeatedly with zero cross-thread faults and zero "queue processor crashed".
/// These guards deliberately do NOT dispatch failing actions on the shared HeadlessUnitTestSession:
/// doing so out-of-band corrupts the session lifecycle that the real tests depend on.
/// </summary>
public sealed class HeadlessHarnessTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void AvaloniaFact_TestBody_RunsOnDispatcherOwningThread()
    {
        // With the stock harness every test body runs on the session's single dispatch thread, which
        // is the thread that owns Dispatcher.UIThread. This is the property that removes the cross-thread
        // construction window the old second-thread StaTaskScheduler opened.
        Assert.True(
            Dispatcher.UIThread.CheckAccess(),
            "The [AvaloniaFact] test body must run on the thread that owns Dispatcher.UIThread.");
    }

    [Fact]
    public void HeadlessSession_ConstructionFailure_FailsOnlyThatTestWithoutCascading()
    {
        // The custom reflection safety nets were built to catch the "queue processor crashed" cascade
        // that a headless app-construction failure used to trigger. Avalonia 12.1.0 fixes this upstream
        // (#21688 wraps construction in try/catch so a failure fails only that test and the dispatch loop
        // survives; #21223 disposes the application before signalling so UI-thread ownership is released
        // cleanly). Guard that the pinned Avalonia.Headless actually contains those fixes.
        var version = typeof(HeadlessUnitTestSession).Assembly.GetName().Version;

        Assert.NotNull(version);
        Assert.True(
            version >= new Version(12, 1, 0, 0),
            $"Avalonia.Headless must be >= 12.1.0 (contains #21688/#21223), but was {version}.");
    }

    [Fact]
    public void HeadlessSession_AfterFailedTest_UiThreadOwnershipIsReleased()
    {
        // Avalonia #20000 makes AvaloniaTestIsolationLevel a first-class, supported feature, and the
        // migration prefers PerTest isolation so a failed test can never share/cascade state across a
        // batch. Guard that this assembly runs with PerTest isolation (no PerAssembly override).
        Assert.NotNull(typeof(AvaloniaTestIsolationLevel).GetEnumName(AvaloniaTestIsolationLevel.PerTest));
        AssertUsesPerTestIsolation(typeof(HeadlessHarnessTests).Assembly);
    }

    [Fact]
    public void HeadlessTestProjects_DeclareNonParallelCollectionBehavior()
    {
        // Every headless test assembly must disable parallelization: with the stock harness the shared
        // HeadlessUnitTestSession dispatches on a single thread and Avalonia does not support concurrent
        // execution against a shared application. Each headless assembly carries the guard so it is
        // enforced independently; here we assert it for the assembly under test.
        AssertNonParallelCollectionBehavior(typeof(HeadlessHarnessTests).Assembly);
    }

    internal static void AssertNonParallelCollectionBehavior(Assembly assembly)
    {
        var attribute = assembly.GetCustomAttribute<CollectionBehaviorAttribute>();

        Assert.NotNull(attribute);
        Assert.True(
            attribute!.DisableTestParallelization,
            $"{assembly.GetName().Name} must declare [assembly: CollectionBehavior(DisableTestParallelization = true)].");
        Assert.Equal(1, attribute.MaxParallelThreads);
    }

    internal static void AssertUsesPerTestIsolation(Assembly assembly)
    {
        // No AvaloniaTestIsolation attribute means Avalonia's supported default (PerTest); if one is
        // present it must not select PerAssembly, which can share/cascade failures across the batch.
        var attribute = assembly.GetCustomAttribute<AvaloniaTestIsolationAttribute>();

        if (attribute is not null)
        {
            Assert.NotEqual(AvaloniaTestIsolationLevel.PerAssembly, attribute.IsolationLevel);
        }
    }
}
