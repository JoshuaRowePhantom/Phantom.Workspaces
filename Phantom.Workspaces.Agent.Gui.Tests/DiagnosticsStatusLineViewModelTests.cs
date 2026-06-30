using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class DiagnosticsStatusLineViewModelTests
{
    [Fact]
    public void DefaultState_HasNoErrors()
    {
        var vm = new DiagnosticsStatusLineViewModel();
        Assert.False(vm.HasErrors);
        Assert.Equal(0, vm.ErrorCount);
    }

    [Fact]
    public void DefaultState_HasNoWarnings()
    {
        var vm = new DiagnosticsStatusLineViewModel();
        Assert.False(vm.HasWarnings);
        Assert.Equal(0, vm.WarningCount);
    }

    [Fact]
    public void HasVisibleContent_IsFalseByDefault()
    {
        var vm = new DiagnosticsStatusLineViewModel();
        Assert.False(vm.HasVisibleContent);
    }

    [Fact]
    public void HasVisibleContent_TrueWhenHasErrors()
    {
        var vm = new DiagnosticsStatusLineViewModel();
        vm.ErrorCount = 1;
        Assert.True(vm.HasVisibleContent);
    }

    [Fact]
    public void HasVisibleContent_TrueWhenHasWarnings()
    {
        var vm = new DiagnosticsStatusLineViewModel();
        vm.WarningCount = 2;
        Assert.True(vm.HasVisibleContent);
    }

    [Fact]
    public void HasErrors_RaisesPropertyChanged_WhenErrorCountBecomesNonZero()
    {
        var vm = new DiagnosticsStatusLineViewModel();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.ErrorCount = 3;

        Assert.Contains(nameof(DiagnosticsStatusLineViewModel.HasErrors), changedProperties);
        Assert.Contains(nameof(DiagnosticsStatusLineViewModel.HasVisibleContent), changedProperties);
        Assert.True(vm.HasErrors);
    }

    [Fact]
    public void HasWarnings_RaisesPropertyChanged_WhenWarningCountBecomesNonZero()
    {
        var vm = new DiagnosticsStatusLineViewModel();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.WarningCount = 1;

        Assert.Contains(nameof(DiagnosticsStatusLineViewModel.HasWarnings), changedProperties);
        Assert.Contains(nameof(DiagnosticsStatusLineViewModel.HasVisibleContent), changedProperties);
        Assert.True(vm.HasWarnings);
    }
}
