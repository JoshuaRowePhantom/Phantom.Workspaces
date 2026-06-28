using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Controls;

/// <summary>
/// A <see cref="UserControl"/> that encapsulates the show → hold → fade → hidden lifecycle
/// for transient popups. Bind a <see cref="TransientPopupViewModel"/> as the DataContext;
/// the control manages opacity timers and pointer-hover cancellation automatically.
/// </summary>
public partial class TransientPopupControl : UserControl
{
    private const double FadeIntervalMs = 50.0;

    private DispatcherTimer? holdTimer;
    private DispatcherTimer? fadeTimer;
    private TransientPopupViewModel? subscribedVm;

    public TransientPopupControl()
    {
        this.InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (this.subscribedVm is not null)
        {
            this.subscribedVm.PropertyChanged -= this.OnViewModelPropertyChanged;
            this.subscribedVm = null;
        }

        if (this.DataContext is TransientPopupViewModel vm)
        {
            this.subscribedVm = vm;
            vm.PropertyChanged += this.OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransientPopupViewModel.IsAutoClosing)
            && sender is TransientPopupViewModel { IsAutoClosing: true })
        {
            this.StartHold();
        }
    }

    private void StartHold()
    {
        this.CancelAll();
        this.Opacity = 1.0;
        if (this.DataContext is not TransientPopupViewModel vm)
        {
            return;
        }

        this.holdTimer = new DispatcherTimer { Interval = vm.HoldDuration };
        this.holdTimer.Tick += this.OnHoldTimerTick;
        this.holdTimer.Start();
    }

    private void OnHoldTimerTick(object? sender, EventArgs e)
    {
        this.CancelHold();
        this.StartFade();
    }

    private void StartFade()
    {
        this.CancelFade();
        if (this.DataContext is not TransientPopupViewModel vm)
        {
            return;
        }

        var decrement = FadeIntervalMs / vm.FadeDuration.TotalMilliseconds;
        this.Opacity = 1.0;
        this.fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FadeIntervalMs) };
        this.fadeTimer.Tick += (_, _) =>
        {
            this.Opacity -= decrement;
            if (this.Opacity <= 0.0)
            {
                this.CancelAll();
                vm.Dismiss();
            }
        };
        this.fadeTimer.Start();
    }

    private void CancelHold()
    {
        if (this.holdTimer is not null)
        {
            this.holdTimer.Stop();
            this.holdTimer.Tick -= this.OnHoldTimerTick;
            this.holdTimer = null;
        }
    }

    private void CancelFade()
    {
        if (this.fadeTimer is not null)
        {
            this.fadeTimer.Stop();
            this.fadeTimer = null;
        }
    }

    private void CancelAll()
    {
        this.CancelHold();
        this.CancelFade();
        this.Opacity = 1.0;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        this.CancelAll();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (this.DataContext is TransientPopupViewModel { IsAutoClosing: true })
        {
            this.StartHold();
        }
    }
}
