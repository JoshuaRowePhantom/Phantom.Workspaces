using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Gui.Shared.Controls;

namespace Phantom.Workspaces.Gui.Shared.Tests;

/// <summary>
/// Headless coverage of <see cref="BrowserAcceleratorBehavior"/> — the routed re-dispatch and
/// KeyBinding walk that replace the direct <c>Command.Execute</c> path from #1168 (see #1189).
/// Uses a lightweight in-process <see cref="TestBrowserHost"/> that implements
/// <see cref="IBrowserAcceleratorSource"/> so tests can drive accelerator events without a real
/// WebView2 adapter, exactly as the design's "headless-friendly test double" contract requires.
/// </summary>
public sealed class BrowserAcceleratorBehaviorTests
{
    /// <summary>A tiny <see cref="Control"/> that implements <see cref="IBrowserAcceleratorSource"/> so tests can raise accelerators.</summary>
    private sealed class TestBrowserHost : Control, IBrowserAcceleratorSource
    {
        public event System.EventHandler<AcceleratorKeyEventArgs>? AcceleratorKeyPressed;

        public void RaiseAccelerator(int kind, Key key, KeyModifiers modifiers) =>
            this.AcceleratorKeyPressed?.Invoke(this, new AcceleratorKeyEventArgs(kind, key, modifiers));

        public void RaiseAccelerator(AcceleratorKeyEventArgs args) =>
            this.AcceleratorKeyPressed?.Invoke(this, args);
    }

    private sealed class RecordingCommand : ICommand
    {
        public int InvokeCount { get; private set; }
#pragma warning disable CS0067 // Event required by ICommand but never raised in tests.
        public event System.EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => this.InvokeCount++;
    }

    private const int KindKeyDown = 0;
    private const int KindKeyUp = 1;
    private const int KindSystemKeyDown = 2;

    [AvaloniaFact]
    public void Behavior_WhenIsEnabledSetTrue_AttachesControllerToNativeWebView()
    {
        var host = new TestBrowserHost();
        Assert.Null(BrowserAcceleratorBehavior.GetController(host));

        BrowserAcceleratorBehavior.SetIsEnabled(host, true);
        var controller = BrowserAcceleratorBehavior.GetController(host);
        Assert.NotNull(controller);

        // Idempotent: setting true again does not replace the existing controller.
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);
        Assert.Same(controller, BrowserAcceleratorBehavior.GetController(host));

        BrowserAcceleratorBehavior.SetIsEnabled(host, false);
        Assert.Null(BrowserAcceleratorBehavior.GetController(host));
        Assert.False(controller!.IsActive);
    }

    [AvaloniaFact]
    public void Behavior_WhenAcceleratorRaisedForCtrlW_RaisesRoutedKeyDownOnWebViewItself()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);

        KeyEventArgs? seen = null;
        host.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => seen = e,
            RoutingStrategies.Bubble);

        host.RaiseAccelerator(KindKeyDown, Key.W, KeyModifiers.Control);

        Assert.NotNull(seen);
        Assert.Equal(Key.W, seen!.Key);
        Assert.Equal(KeyModifiers.Control, seen.KeyModifiers);
        Assert.Same(host, seen.Source);
    }

    [AvaloniaFact]
    public void Behavior_WebViewRaisesBubblingKeyDown_AncestorReceivesItViaAddHandler()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);

        var received = false;
        parent.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control)
                {
                    received = true;
                }
            },
            RoutingStrategies.Bubble);

        host.RaiseAccelerator(KindKeyDown, Key.W, KeyModifiers.Control);

        Assert.True(received);
    }

    [AvaloniaFact]
    public void Behavior_WebViewRaisesTunnelKeyDown_AncestorTunnelHandlerFires()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);

        var tunnelFired = false;
        parent.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control)
                {
                    tunnelFired = true;
                }
            },
            RoutingStrategies.Tunnel);

        host.RaiseAccelerator(KindKeyDown, Key.W, KeyModifiers.Control);

        Assert.True(tunnelFired);
    }

    [AvaloniaFact]
    public void Behavior_CtrlWAcceleratorOnWebView_InvokesTopLevelCloseActiveTabCommandExactlyOnce()
    {
        var host = new TestBrowserHost();
        var closeCommand = new RecordingCommand();
        var window = new Window
        {
            Content = host,
            KeyBindings =
            {
                new KeyBinding
                {
                    Gesture = new KeyGesture(Key.W, KeyModifiers.Control),
                    Command = closeCommand,
                },
            },
        };
        window.Show();
        try
        {
            BrowserAcceleratorBehavior.SetIsEnabled(host, true);

            host.RaiseAccelerator(KindKeyDown, Key.W, KeyModifiers.Control);

            Assert.Equal(1, closeCommand.InvokeCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Behavior_AltHoldAcceleratorOnWebView_ReachesDockTabSwitchController()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);

        // Simulate a tunnel modifier-state observer on an ancestor (this is what
        // DockTabSwitchController does on its DockControl / TopLevel — see
        // DockTabSwitchController.cs:328-346 for the analogous RefreshBadgeVisibility path).
        var heldAlt = false;
        parent.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => { if (e.Key == Key.LeftAlt) heldAlt = true; },
            RoutingStrategies.Tunnel);
        parent.AddHandler(
            InputElement.KeyUpEvent,
            (_, e) => { if (e.Key == Key.LeftAlt) heldAlt = false; },
            RoutingStrategies.Tunnel);

        host.RaiseAccelerator(KindSystemKeyDown, Key.LeftAlt, KeyModifiers.Alt);
        Assert.True(heldAlt);

        host.RaiseAccelerator(KindKeyUp, Key.LeftAlt, KeyModifiers.Alt);
        Assert.False(heldAlt);
    }

    [AvaloniaFact]
    public void Behavior_AltDigitAcceleratorOnWebView_InvokesDockTabSwitchActivate()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);

        var activatedIndex = -1;
        parent.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.KeyModifiers == KeyModifiers.Alt && e.Key >= Key.D1 && e.Key <= Key.D9)
                {
                    activatedIndex = (int)e.Key - (int)Key.D1;
                    e.Handled = true;
                }
            },
            RoutingStrategies.Tunnel);

        host.RaiseAccelerator(KindSystemKeyDown, Key.D1, KeyModifiers.Alt);

        Assert.Equal(0, activatedIndex);
    }

    [AvaloniaFact]
    public void Behavior_AltAutoRepeatSystemKeyDown_DispatchedEachRepeatAsRoutedKeyDown()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);

        var count = 0;
        parent.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => { if (e.Key == Key.LeftAlt) count++; },
            RoutingStrategies.Tunnel);

        for (var i = 0; i < 5; i++)
        {
            host.RaiseAccelerator(KindSystemKeyDown, Key.LeftAlt, KeyModifiers.Alt);
        }

        Assert.Equal(5, count);
    }

    [AvaloniaFact]
    public void Behavior_WhenAcceleratorHasNoRoutedOrKeyBindingConsumer_LeavesEventUnhandled()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);

        var args = new AcceleratorKeyEventArgs(KindKeyDown, Key.A, KeyModifiers.None);
        host.RaiseAccelerator(args);

        Assert.False(args.Handled);
    }

    [AvaloniaFact]
    public void Behavior_WhenIsEnabledSetFalse_StopsForwardingSubsequentAccelerators()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        BrowserAcceleratorBehavior.SetIsEnabled(host, true);

        var count = 0;
        host.AddHandler(
            InputElement.KeyDownEvent,
            (_, _) => count++,
            RoutingStrategies.Bubble);

        host.RaiseAccelerator(KindKeyDown, Key.W, KeyModifiers.Control);
        Assert.Equal(1, count);

        BrowserAcceleratorBehavior.SetIsEnabled(host, false);
        host.RaiseAccelerator(KindKeyDown, Key.W, KeyModifiers.Control);
        Assert.Equal(1, count);
    }

    [AvaloniaFact]
    public void Behavior_WhenWebViewDetachedFromVisualTree_DisposesSubscription()
    {
        var host = new TestBrowserHost();
        var parent = new StackPanel();
        parent.Children.Add(host);
        var window = new Window { Content = parent };
        window.Show();
        try
        {
            BrowserAcceleratorBehavior.SetIsEnabled(host, true);
            var controller = BrowserAcceleratorBehavior.GetController(host);
            Assert.NotNull(controller);
            Assert.True(controller!.IsActive);

            // Detach from the visual tree.
            parent.Children.Remove(host);

            Assert.False(controller.IsActive);
        }
        finally
        {
            window.Close();
        }
    }
}
