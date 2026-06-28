using System.Collections.Generic;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class TransientPopupViewModelTests
{
    private sealed class ConcretePopupViewModel : TransientPopupViewModel
    {
        public int DismissedCallCount { get; private set; }

        protected override void OnDismissed() => this.DismissedCallCount++;
    }

    private sealed class CustomDurationPopupViewModel : TransientPopupViewModel
    {
        public CustomDurationPopupViewModel()
        {
            this.HoldDuration = System.TimeSpan.FromMilliseconds(500);
            this.FadeDuration = System.TimeSpan.FromMilliseconds(300);
        }
    }

    [Fact]
    public void Show_SetsIsOpenTrue()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();
        Assert.True(vm.IsOpen);
    }

    [Fact]
    public void Show_SetsIsAutoClosingTrue()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();
        Assert.True(vm.IsAutoClosing);
    }

    [Fact]
    public void Dismiss_SetsIsOpenFalse()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();
        vm.Dismiss();
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void Dismiss_SetsIsAutoClosingFalse()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();
        vm.Dismiss();
        Assert.False(vm.IsAutoClosing);
    }

    [Fact]
    public void OnDismissed_CalledAfterDismiss()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();
        vm.Dismiss();
        Assert.Equal(1, vm.DismissedCallCount);
    }

    [Fact]
    public void OnDismissed_NotCalledAfterShow()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();
        Assert.Equal(0, vm.DismissedCallCount);
    }

    [Fact]
    public void Show_WhileVisible_AlwaysFiresPropertyChangedForIsAutoClosing()
    {
        var vm = new ConcretePopupViewModel();
        var firedNames = new List<string?>();
        vm.PropertyChanged += (_, args) => firedNames.Add(args.PropertyName);

        vm.Show();
        vm.Show();

        Assert.Equal(2, firedNames.FindAll(n => n == nameof(vm.IsAutoClosing)).Count);
    }

    [Fact]
    public void HoldDuration_DefaultIs2000ms()
    {
        var vm = new ConcretePopupViewModel();
        Assert.Equal(System.TimeSpan.FromMilliseconds(2000), vm.HoldDuration);
    }

    [Fact]
    public void FadeDuration_DefaultIs750ms()
    {
        var vm = new ConcretePopupViewModel();
        Assert.Equal(System.TimeSpan.FromMilliseconds(750), vm.FadeDuration);
    }

    [Fact]
    public void CustomDurations_AreRespected()
    {
        var vm = new CustomDurationPopupViewModel();
        Assert.Equal(System.TimeSpan.FromMilliseconds(500), vm.HoldDuration);
        Assert.Equal(System.TimeSpan.FromMilliseconds(300), vm.FadeDuration);
    }

    [Fact]
    public void PropertyChanged_FiresForIsOpen_OnShow()
    {
        var vm = new ConcretePopupViewModel();
        var firedNames = new List<string?>();
        vm.PropertyChanged += (_, args) => firedNames.Add(args.PropertyName);

        vm.Show();

        Assert.Contains(nameof(vm.IsOpen), firedNames);
    }

    [Fact]
    public void PropertyChanged_FiresForIsAutoClosing_OnShow()
    {
        var vm = new ConcretePopupViewModel();
        var firedNames = new List<string?>();
        vm.PropertyChanged += (_, args) => firedNames.Add(args.PropertyName);

        vm.Show();

        Assert.Contains(nameof(vm.IsAutoClosing), firedNames);
    }

    [Fact]
    public void PropertyChanged_FiresForIsOpen_OnDismiss()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();

        var firedNames = new List<string?>();
        vm.PropertyChanged += (_, args) => firedNames.Add(args.PropertyName);

        vm.Dismiss();

        Assert.Contains(nameof(vm.IsOpen), firedNames);
    }

    [Fact]
    public void PropertyChanged_FiresForIsAutoClosing_OnDismiss()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();

        var firedNames = new List<string?>();
        vm.PropertyChanged += (_, args) => firedNames.Add(args.PropertyName);

        vm.Dismiss();

        Assert.Contains(nameof(vm.IsAutoClosing), firedNames);
    }

    [Fact]
    public void Dismiss_WhenAlreadyHidden_DoesNotFirePropertyChanged()
    {
        var vm = new ConcretePopupViewModel();
        var firedNames = new List<string?>();
        vm.PropertyChanged += (_, args) => firedNames.Add(args.PropertyName);

        vm.Dismiss();

        Assert.Empty(firedNames);
    }

    [Fact]
    public void NotifyLightDismissed_ResetsIsAutoClosingToFalse()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();
        vm.NotifyLightDismissed();
        Assert.False(vm.IsAutoClosing);
    }

    [Fact]
    public void NotifyLightDismissed_CallsOnDismissed()
    {
        var vm = new ConcretePopupViewModel();
        vm.Show();
        vm.NotifyLightDismissed();
        Assert.Equal(1, vm.DismissedCallCount);
    }
}
