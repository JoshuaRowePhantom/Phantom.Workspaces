using System;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Base class for view models that back a "show → hold → fade → hidden" transient overlay.
/// Subclasses call <see cref="Show"/> when a trigger fires and override
/// <see cref="OnDismissed"/> to react when the popup has fully faded out.
/// </summary>
public abstract class TransientPopupViewModel : ViewModelBase
{
    private bool isOpen;
    private bool isAutoClosing;

    /// <summary>Time the popup stays at full opacity after the last <see cref="Show"/> call.</summary>
    public TimeSpan HoldDuration { get; protected set; } = TimeSpan.FromMilliseconds(2000);

    /// <summary>Duration of the opacity fade from 1.0 → 0.0.</summary>
    public TimeSpan FadeDuration { get; protected set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Whether the popup should be open.
    /// Bound two-way to the Avalonia Popup's <c>IsOpen</c>.
    /// </summary>
    public bool IsOpen
    {
        get => this.isOpen;
        set => this.SetProperty(ref this.isOpen, value);
    }

    /// <summary>
    /// Set to <c>true</c> when a trigger fires; the control watches this to start
    /// the hold + fade sequence. Reset to <c>false</c> when the popup has fully
    /// faded out or when the popup is manually opened (non-auto-closing).
    /// </summary>
    public bool IsAutoClosing
    {
        get => this.isAutoClosing;
        set => this.SetProperty(ref this.isAutoClosing, value);
    }

    /// <summary>
    /// Show the popup and (re-)start the hold → fade sequence.
    /// Safe to call while the popup is already visible: always restarts the hold timer.
    /// </summary>
    public void Show()
    {
        this.IsOpen = true;
        // Bypass SetProperty to force PropertyChanged on every Show() call,
        // ensuring the control always restarts its hold timer even on re-triggers.
        this.isAutoClosing = true;
        this.RaisePropertyChanged(nameof(this.IsAutoClosing));
    }

    /// <summary>
    /// Trigger the hold → fade close sequence without changing <see cref="IsOpen"/>.
    /// Use this when a user action should let the popup fade naturally instead of closing immediately.
    /// Safe to call while the hold timer is already running: always restarts it from the full duration.
    /// </summary>
    public void TriggerFadeClose()
    {
        // Bypass SetProperty to force PropertyChanged on every call, so the control always
        // restarts its hold timer — even when IsAutoClosing is already true.
        this.isAutoClosing = true;
        this.RaisePropertyChanged(nameof(this.IsAutoClosing));
    }

    /// <summary>Immediately hide the popup without fading.</summary>
    public void Dismiss()
    {
        if (!this.isOpen && !this.isAutoClosing)
        {
            return;
        }

        this.IsOpen = false;
        this.IsAutoClosing = false;
        this.OnDismissed();
    }

    /// <summary>
    /// Call this when Avalonia's light-dismiss sets <c>IsOpen = false</c> externally,
    /// to ensure <see cref="IsAutoClosing"/> is cleared and <see cref="OnDismissed"/> fires.
    /// </summary>
    public void NotifyLightDismissed()
    {
        this.isAutoClosing = false;
        this.RaisePropertyChanged(nameof(this.IsAutoClosing));
        this.OnDismissed();
    }

    /// <summary>
    /// Called when the popup has been dismissed (either by fade completion or by <see cref="Dismiss"/>).
    /// Override to perform cleanup (e.g. clear highlights).
    /// </summary>
    protected virtual void OnDismissed() { }
}
