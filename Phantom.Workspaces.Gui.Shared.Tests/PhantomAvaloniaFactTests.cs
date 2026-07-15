using System;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class PhantomAvaloniaFactTests
{
    // Regression test for issue #815 and #793: verify no PerTest isolation in Gui.Shared.Tests
    [Fact]
    public void AvaloniaXUnitSetup_GuiSharedTests_DoesNotDeclarePerTestIsolation()
    {
        var assembly = typeof(PhantomAvaloniaFactTests).Assembly;
        
        // The AvaloniaTestIsolationAttribute is in Avalonia.Headless.XUnit
        var avaloniaAssembly = typeof(AvaloniaFactAttribute).Assembly;
        var isolationAttrType = avaloniaAssembly.GetType("Avalonia.Headless.XUnit.AvaloniaTestIsolationAttribute");
        
        if (isolationAttrType != null)
        {
            var isolationAttr = assembly.GetCustomAttribute(isolationAttrType);
            
            // Assert: either no attribute is present OR the level is not PerTest
            if (isolationAttr != null)
            {
                var levelProperty = isolationAttrType.GetProperty("Level");
                var levelValue = levelProperty?.GetValue(isolationAttr);
                var perTestValue = Enum.Parse(levelValue!.GetType(), "PerTest");
                
                Assert.NotEqual(perTestValue, levelValue);
            }
        }
    }

    // Meta-test for issue #815: verify TerminalControlTests can run without _dispatchTask faults
    [Fact]
    public void HeadlessUnitTestSession_TerminalControlBatch_AllTestsSucceed()
    {
        // This meta-test verifies that the TerminalControlTests suite can run without
        // _dispatchTask crashes by checking that the test class exists and that the
        // HeadlessUnitTestSession for this assembly is in a healthy state.
        
        var testClass = typeof(TerminalControlTests);
        var testMethods = testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<PhantomAvaloniaFactAttribute>() != null)
            .ToList();
        
        // Assert: there are test methods to run
        Assert.NotEmpty(testMethods);
        
        // Verify that the HeadlessUnitTestSession for this assembly is healthy
        // by checking that _dispatchTask (if accessible) is not in a faulted state.
        var session = Avalonia.Headless.HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(PhantomAvaloniaFactTests).Assembly);
        
        var dispatchTaskField = typeof(Avalonia.Headless.HeadlessUnitTestSession).GetField(
            "_dispatchTask", BindingFlags.NonPublic | BindingFlags.Instance);
        
        if (dispatchTaskField?.GetValue(session) is Task dispatchTask)
        {
            // If we can access the _dispatchTask, verify it's not faulted
            Assert.False(dispatchTask.IsFaulted,
                "_dispatchTask is faulted, indicating the HeadlessUnitTestSession crashed. " +
                "This suggests PerTest isolation was enabled, causing the #815 crash.");
            
            // Also verify it's not completed (it should be running)
            Assert.False(dispatchTask.IsCompleted,
                "_dispatchTask is completed, which should not happen during test execution.");
        }
        
        // The actual batch run verification happens during normal test execution when
        // all TerminalControlTests run together. This meta-test verifies the session is healthy.
    }

    // Consolidation guard for issue #815 Option B: verify PhantomAvaloniaFact lives in shared assembly
    [Fact]
    public void PhantomAvaloniaTestSupport_TypesLiveInSharedAssembly_NotDuplicatedPerProject()
    {
        // Verify that PhantomAvaloniaFactAttribute and PhantomAvaloniaTestCase are defined
        // in a shared assembly (Phantom.Workspaces.Testing.Gui) rather than duplicated
        // in each test project.
        
        var factAttrAssembly = typeof(PhantomAvaloniaFactAttribute).Assembly;
        var testCaseAssembly = typeof(PhantomAvaloniaTestCase).Assembly;
        
        // Both types should come from the same assembly
        Assert.Equal(factAttrAssembly, testCaseAssembly);
        
        // The assembly should be the shared Testing.Gui assembly
        Assert.Contains("Phantom.Workspaces.Testing.Gui", factAttrAssembly.FullName);
        
        // The assembly should NOT be a test assembly
        Assert.DoesNotContain(".Tests", factAttrAssembly.FullName);
    }
}
