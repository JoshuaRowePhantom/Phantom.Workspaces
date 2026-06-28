using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Controls;

public partial class NotificationsControl : UserControl
{
    private const double HoldDurationMs = 2000.0;
    private const double FadeIntervalMs = 50.0;
    private const double FadeDurationMs = 750.0;
    private const double OpacityDecrementPerTick = FadeIntervalMs / FadeDurationMs;

    private DispatcherTimer? holdTimer;
    private DispatcherTimer? fadeTimer;

    public NotificationsControl()
    {
        this.InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.DataContext is NotificationsViewModel vm)
        {
            vm.PropertyChanged += this.OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationsViewModel.IsAutoClosing)
            && sender is NotificationsViewModel { IsAutoClosing: true })
        {
            this.StartHold();
        }
    }

    private void StartHold()
    {
        this.CancelAll();
        this.Opacity = 1.0;
        this.holdTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(HoldDurationMs) };
        this.holdTimer.Tick += this.OnHoldTimerTick;
        this.holdTimer.Start();
    }

    private void OnHoldTimerTick(object? sender, System.EventArgs e)
    {
        this.CancelHold();
        this.StartFade();
    }

    private void StartFade()
    {
        this.CancelFade();
        this.Opacity = 1.0;
        this.fadeTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(FadeIntervalMs) };
        this.fadeTimer.Tick += this.OnFadeTimerTick;
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
            this.fadeTimer.Tick -= this.OnFadeTimerTick;
            this.fadeTimer = null;
        }
    }

    private void CancelAll()
    {
        this.CancelHold();
        this.CancelFade();
        this.Opacity = 1.0;
    }

    private void OnFadeTimerTick(object? sender, System.EventArgs e)
    {
        this.Opacity -= OpacityDecrementPerTick;
        if (this.Opacity <= 0.0)
        {
            this.CancelAll();
            if (this.DataContext is NotificationsViewModel vm)
            {
                vm.IsOpen = false;
                vm.IsAutoClosing = false;
            }
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        this.CancelAll();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (this.DataContext is NotificationsViewModel { IsAutoClosing: true })
        {
            this.StartHold();
        }
    }
}
