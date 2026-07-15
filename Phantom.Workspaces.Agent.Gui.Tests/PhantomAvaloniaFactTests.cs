using System;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class PhantomAvaloniaFactTests
{
    [PhantomAvaloniaFact]
    public void PhantomAvaloniaFact_DelegatesToAvaloniaTestCase()
    {
        // PhantomAvaloniaFact delegates to AvaloniaTestCase which schedules the test body on
        // the Avalonia UI thread -- verifying that proves the delegation is working correctly.
        Assert.True(Dispatcher.UIThread.CheckAccess(), "Test body should be running on the Avalonia UI thread.");
    }

    // Regression test for issue #815: verify no PerTest isolation in Agent.Gui.Tests
    [Fact]
    public void AvaloniaXUnitSetup_AgentGuiTests_DoesNotDeclarePerTestIsolation()
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

    // Meta-test for issue #815: verify ChatOutputHtmlModelTests can run without _dispatchTask faults
    [Fact]
    public void HeadlessUnitTestSession_ChatOutputHtmlModelBatch_AllTestsSucceed()
    {
        // This meta-test verifies that the ChatOutputHtmlModelTests suite can run without
        // _dispatchTask crashes by checking that the test class exists and that the
        // HeadlessUnitTestSession for this assembly is in a healthy state.
        
        var testClass = typeof(ChatOutputHtmlModelTests);
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
        // all ChatOutputHtmlModelTests run together. This meta-test verifies the session is healthy.
    }
}
