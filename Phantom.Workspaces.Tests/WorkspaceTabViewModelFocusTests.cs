using System;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspaceTabViewModelFocusTests
{
    [Fact]
    public void RequestFocusPrimaryControl_RaisesFocusPrimaryControlRequested()
    {
        var vm = new WebViewModel("https://example.com") { Id = "focus-test-1", Title = "T" };
        var raised = false;
        vm.FocusPrimaryControlRequested += (_, _) => raised = true;

        vm.RequestFocusPrimaryControl();

        Assert.True(raised);
    }

    [Fact]
    public void AgentSessionWorkspaceTabViewModel_WhenAgentIsNull_RequestFocusPrimaryControl_RaisesBaseEvent()
    {
        var vm = new AgentSessionWorkspaceTabViewModel { Id = "focus-test-2", Title = "T" };
        var raised = false;
        vm.FocusPrimaryControlRequested += (_, _) => raised = true;

        vm.RequestFocusPrimaryControl();

        Assert.True(raised);
    }
}
