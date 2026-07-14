using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class HeadlessUnitTestSessionIsolationTests
{
    [Fact]
    public void PhantomAvaloniaTestSupport_TypesLiveInSharedAssembly_NotDuplicatedPerProject()
    {
        // Verify that PhantomAvaloniaFactAttribute and PhantomAvaloniaTestCase resolve from a single
        // shared assembly (Phantom.Workspaces.Testing.Gui) rather than existing as duplicates in each
        // *.Tests assembly. This is the consolidation guard for Option B (#815).
        
        var factAttributeType = typeof(PhantomAvaloniaFactAttribute);
        var testCaseType = typeof(PhantomAvaloniaTestCase);
        
        // Both types should be in the Phantom.Workspaces.Testing.Gui assembly
        Assert.Equal("Phantom.Workspaces.Testing.Gui", factAttributeType.Assembly.GetName().Name);
        Assert.Equal("Phantom.Workspaces.Testing.Gui", testCaseType.Assembly.GetName().Name);
        
        // Verify that they're in the same assembly
        Assert.Equal(factAttributeType.Assembly, testCaseType.Assembly);
    }
}
