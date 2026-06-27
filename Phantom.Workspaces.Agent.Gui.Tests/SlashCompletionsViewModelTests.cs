using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm.SlashCommands;
using System.Collections.Generic;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class SlashCompletionsViewModelTests
{
    private static IReadOnlyList<SlashCommandCompletion> MakeItems(params string[] names)
        => names.Select(n => new SlashCommandCompletion(n, $"/{n}", $"desc {n}")).ToArray();

    [Fact]
    public void SetItems_WithItems_SetsIsVisibleTrue()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha", "beta"));
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void SetItems_WithEmptyList_SetsIsVisibleFalse()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha"));
        vm.SetItems([]);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void SelectNext_WhenNoItemSelected_SelectsFirst()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha", "beta"));
        vm.SelectNext();
        Assert.Equal(0, vm.SelectedIndex);
        Assert.Equal("alpha", vm.SelectedItem!.CompletionText);
    }

    [Fact]
    public void SelectNext_WhenLastItemSelected_WrapsToFirst()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha", "beta", "gamma"));
        vm.SelectedIndex = 2;
        vm.SelectNext();
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void SelectPrevious_WhenFirstItemSelected_WrapsToLast()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha", "beta", "gamma"));
        vm.SelectedIndex = 0;
        vm.SelectPrevious();
        Assert.Equal(2, vm.SelectedIndex);
    }

    [Fact]
    public void SelectPrevious_WhenNoItemSelected_SelectsLast()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha", "beta", "gamma"));
        vm.SelectPrevious();
        Assert.Equal(2, vm.SelectedIndex);
    }

    [Fact]
    public void Accept_WithSelectedItem_ReturnsCompletionTextAndDismisses()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha", "beta"));
        vm.SelectedIndex = 1;
        var result = vm.Accept();
        Assert.Equal("beta", result);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void Accept_WithNoSelection_ReturnsNullAndDismisses()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha"));
        var result = vm.Accept();
        Assert.Null(result);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void Dismiss_HidesPopup()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha"));
        vm.Dismiss();
        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void SetItems_ReplacesPreviousItems()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha", "beta"));
        vm.SetItems(MakeItems("gamma"));
        Assert.Single(vm.Items);
        Assert.Equal("gamma", vm.Items[0].CompletionText);
    }

    [Fact]
    public void SetItems_ResetsSelectedIndex()
    {
        var vm = new SlashCompletionsViewModel();
        vm.SetItems(MakeItems("alpha", "beta"));
        vm.SelectedIndex = 1;
        vm.SetItems(MakeItems("alpha", "beta"));
        Assert.Equal(-1, vm.SelectedIndex);
        Assert.Null(vm.SelectedItem);
    }
}
