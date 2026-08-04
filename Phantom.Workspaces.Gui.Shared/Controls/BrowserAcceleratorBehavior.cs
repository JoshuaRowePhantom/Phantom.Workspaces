using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Static attached-property host that opts a browser-hosting control into re-entering the Avalonia
/// routed input pipeline for every WebView2 accelerator-key event. Setting
/// <see cref="IsEnabledProperty"/> to <c>true</c> installs an internal <see cref="BrowserAcceleratorController"/>;
/// setting it to <c>false</c> (or leaving it default) tears it down. Follows the codebase
/// attached-property-drives-behavior idiom (see <see cref="Phantom.Dock.Avalonia.TabSwitching.DockTabSwitch"/>).
/// See issue #1189 for the motivating regression.
/// </summary>
public static class BrowserAcceleratorBehavior
{
    /// <summary>
    /// When <c>true</c> on a browser-hosting <see cref="Control"/> (typically a <c>NativeWebView</c>),
    /// installs a <see cref="BrowserAcceleratorController"/> that forwards WebView2 accelerator keys
    /// as routed <c>KeyDown</c>/<c>KeyUp</c> events on the control itself and walks visual-tree
    /// ancestors to invoke any matching <c>KeyBinding</c> (mirroring Avalonia
    /// <c>KeyboardDevice.ProcessRawEvent</c>).
    /// </summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(BrowserAcceleratorBehavior));

    /// <summary>
    /// Private storage for the controller instance on the host so <see cref="IsEnabledProperty"/>
    /// toggles are idempotent (never double-attach or leak).
    /// </summary>
    private static readonly AttachedProperty<BrowserAcceleratorController?> ControllerProperty =
        AvaloniaProperty.RegisterAttached<Control, BrowserAcceleratorController?>(
            "Controller", typeof(BrowserAcceleratorBehavior));

    static BrowserAcceleratorBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);
    }

    public static void SetIsEnabled(Control control, bool value) =>
        control.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    /// <summary>The controller installed on <paramref name="control"/>, or <c>null</c> if not enabled.</summary>
    internal static BrowserAcceleratorController? GetController(Control control) =>
        control.GetValue(ControllerProperty);

    /// <summary>
    /// Dispatches an accelerator into the routed input pipeline on <paramref name="host"/>: raises a
    /// <see cref="InputElement.KeyDownEvent"/> or <see cref="InputElement.KeyUpEvent"/> with
    /// <c>Source == host</c>, then — mirroring <c>KeyboardDevice.ProcessRawEvent</c> — walks visual
    /// ancestors invoking any matching <c>KeyBinding</c>. KeyBindings do not fire from
    /// <see cref="Interactive.RaiseEvent"/>, so the explicit walk is required for
    /// <c>Window</c>-level bindings such as <c>Ctrl+W</c>.
    /// </summary>
    public static void Dispatch(Control host, AcceleratorKeyEventArgs e)
    {
        if (e is null || e.Key == Key.None)
        {
            return;
        }

        var routedEvent = e.IsKeyDown ? InputElement.KeyDownEvent : InputElement.KeyUpEvent;
        var args = new KeyEventArgs
        {
            RoutedEvent = routedEvent,
            Source = host,
            Key = e.Key,
            KeyModifiers = e.Modifiers,
        };
        host.RaiseEvent(args);

        // KeyBindings are NOT part of the routed event flow (see KeyboardDevice.ProcessRawEvent).
        // Mirror the same semantics so Window-level bindings such as Ctrl+W fire from
        // re-dispatched WebView accelerators too.
        if (!args.Handled && e.IsKeyDown)
        {
            for (Visual? cursor = host; cursor is not null && !args.Handled; cursor = cursor.GetVisualParent())
            {
                if (cursor is not IInputElement inputElement)
                {
                    continue;
                }

                foreach (var binding in inputElement.KeyBindings)
                {
                    if (binding.Gesture is { } gesture &&
                        gesture.Key == e.Key &&
                        gesture.KeyModifiers == e.Modifiers)
                    {
                        var command = binding.Command;
                        if (command is not null && command.CanExecute(binding.CommandParameter))
                        {
                            command.Execute(binding.CommandParameter);
                            args.Handled = true;
                            break;
                        }
                    }
                }
            }
        }

        e.Handled = args.Handled;
    }

    private static void OnIsEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var existing = control.GetValue(ControllerProperty);
        if (e.GetNewValue<bool>())
        {
            if (existing is not null)
            {
                return;
            }

            var controller = new BrowserAcceleratorController(control);
            control.SetValue(ControllerProperty, controller);
            controller.Attach();
        }
        else
        {
            if (existing is null)
            {
                return;
            }

            control.SetValue(ControllerProperty, null);
            existing.Dispose();
        }
    }
}

/// <summary>
/// Owns the lifetime of the WebView2 accelerator-key COM subscription (or, for an
/// <see cref="IBrowserAcceleratorSource"/> host, the managed subscription) and re-raises every
/// received accelerator through <see cref="BrowserAcceleratorBehavior.Dispatch"/>.
/// </summary>
internal sealed class BrowserAcceleratorController : IDisposable
{
    private readonly Control host;
    private EventHandler<AcceleratorKeyEventArgs>? sourceHandler;
    private EventHandler<VisualTreeAttachmentEventArgs>? detachedHandler;
    private bool disposed;

    public BrowserAcceleratorController(Control host)
    {
        this.host = host;
    }

    /// <summary>True while the controller is still forwarding events; set to false in <see cref="Dispose"/>.</summary>
    internal bool IsActive => !this.disposed;

    public void Attach()
    {
        // Preferred path: if the host already exposes AcceleratorKeyPressed (production is
        // AcceleratorAwareWebView, headless is a test double), subscribe to the managed event.
        // AcceleratorAwareWebView itself owns the SDK-typed COM subscription (see #1208), so
        // there is no reflection or adapter-level fallback here — a host that does not implement
        // IBrowserAcceleratorSource is a configuration error and is traced.
        if (this.host is IBrowserAcceleratorSource src)
        {
            this.sourceHandler = (_, e) => this.OnAccelerator(e);
            src.AcceleratorKeyPressed += this.sourceHandler;
        }
        else
        {
            Trace.TraceWarning(
                "BrowserAcceleratorController: host {0} does not implement IBrowserAcceleratorSource; "
                + "accelerator forwarding will be inactive.",
                this.host.GetType().FullName);
        }

        this.detachedHandler = (_, _) => this.Dispose();
        this.host.DetachedFromVisualTree += this.detachedHandler;
    }

    private void OnAccelerator(AcceleratorKeyEventArgs e)
    {
        if (this.disposed)
        {
            return;
        }

        BrowserAcceleratorBehavior.Dispatch(this.host, e);
    }

    /// <summary>
    /// Test-only hook: raises an accelerator through this controller as if it had come from the
    /// COM subscription. Respects the disposed flag so
    /// <see cref="BrowserAcceleratorBehavior.SetIsEnabled(Control,bool)"/>-driven teardown wins.
    /// </summary>
    internal void SimulateAccelerator(AcceleratorKeyEventArgs e) => this.OnAccelerator(e);

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        if (this.sourceHandler is not null && this.host is IBrowserAcceleratorSource src)
        {
            src.AcceleratorKeyPressed -= this.sourceHandler;
        }

        if (this.detachedHandler is not null)
        {
            this.host.DetachedFromVisualTree -= this.detachedHandler;
        }

        this.sourceHandler = null;
        this.detachedHandler = null;
    }
}
