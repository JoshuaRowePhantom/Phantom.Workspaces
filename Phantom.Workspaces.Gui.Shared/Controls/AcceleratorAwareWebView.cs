using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Abstract base class that sits between <see cref="NativeWebView"/> and concrete web-view
/// controls. Subscribes to <see cref="NativeWebView.AdapterCreated"/> once the WebView2
/// platform adapter is ready, wires up the COM-level <c>AcceleratorKeyPressed</c> hook via the
/// public <see cref="NativeWebView.TryGetPlatformHandle"/> path and the SDK-shipped raw
/// <c>Microsoft.Web.WebView2.Core.Raw</c> interfaces (no reflection into Avalonia internals),
/// and exposes events that subclasses (and their owners) can subscribe to. See #1208.
/// </summary>
public abstract class AcceleratorAwareWebView : NativeWebView, IBrowserAcceleratorSource
{
    private IDisposable? acceleratorSubscription;
    private const int VkMenu = 0x12;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkDigit0 = 0x30;
    private const int VkDigit9 = 0x39;
    private const int VkW = 0x57;

    protected AcceleratorAwareWebView()
    {
        this.AdapterCreated += this.OnAdapterCreated;
        this.AdapterDestroyed += this.OnAdapterDestroyed;
    }

    /// <summary>Raised on the UI thread when Alt is pressed (true) or released (false) inside this WebView.</summary>
    public event EventHandler<bool>? AltKeyStateChanged;

    /// <summary>Raised on the UI thread when Alt+N is pressed inside this WebView. Argument is 0-based tab index.</summary>
    public event EventHandler<int>? GoToTabAtIndexRequested;

    /// <summary>Raised on the UI thread when Alt+Shift+N is pressed inside this WebView. Argument is 0-based workspace pane index.</summary>
    public event EventHandler<int>? GoToWorkspacePaneAtIndexRequested;

    // #1310: The legacy CloseTabRequested event was removed. Ctrl+W is handled exclusively
    // by the generic AcceleratorKeyPressed forwarder, which invokes the top-level
    // CloseActiveTabCommand KeyBinding via BrowserAcceleratorBehavior. Having a separate
    // typed event caused the accelerator to close two tabs (one per path) for a single key.

    /// <inheritdoc/>
    public event EventHandler<AcceleratorKeyEventArgs>? AcceleratorKeyPressed;

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        this.SubscribeAcceleratorKeyPressed();
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        var existing = System.Threading.Interlocked.Exchange(ref this.acceleratorSubscription, null);
        existing?.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private void SubscribeAcceleratorKeyPressed()
    {
        try
        {
            var subscription = WebView2AcceleratorInterop.Subscribe(this, this.OnAcceleratorKeyPressed);
            var previous = System.Threading.Interlocked.Exchange(ref this.acceleratorSubscription, subscription);
            previous?.Dispose();
            if (subscription is null)
            {
                Trace.TraceWarning(
                    "AcceleratorAwareWebView.SubscribeAcceleratorKeyPressed: interop returned null; "
                    + "accelerators inside the WebView will not be forwarded to Avalonia.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "AcceleratorAwareWebView.SubscribeAcceleratorKeyPressed: {0}", ex);
        }
    }

    private void OnAcceleratorKeyPressed(AcceleratorKeyEventArgs args)
    {
        // The generic listener runs synchronously on the COM callback thread so listeners can set
        // Handled before the interop's set_Handled(1) call returns to WebView2. Legacy typed
        // subscribers (AltKeyStateChanged / GoToTab / CloseTab / GoToWorkspacePane) require the
        // UI thread and are dispatched.
        this.AcceleratorKeyPressed?.Invoke(this, args);
        this.FanOutLegacyTypedEvents(args);
    }

    private void FanOutLegacyTypedEvents(AcceleratorKeyEventArgs args)
    {
        var vk = ToVirtualKey(args.Key);

        if (vk == VkMenu)
        {
            if (args.KeyEventKind == CoreWebView2KeyEventKind.SystemKeyDown)
                Dispatcher.UIThread.Post(() => this.AltKeyStateChanged?.Invoke(this, true));
            else if (args.KeyEventKind == CoreWebView2KeyEventKind.SystemKeyUp)
                Dispatcher.UIThread.Post(() => this.AltKeyStateChanged?.Invoke(this, false));
            return;
        }

        if (args.KeyEventKind == CoreWebView2KeyEventKind.SystemKeyDown && vk >= VkDigit0 && vk <= VkDigit9)
        {
            int index = vk == VkDigit0 ? 9 : vk - (VkDigit0 + 1);
            if (WebView2AcceleratorInterop.IsKeyDown(VkShift))
            {
                Dispatcher.UIThread.Post(() => this.GoToWorkspacePaneAtIndexRequested?.Invoke(this, index));
            }
            else
            {
                Dispatcher.UIThread.Post(() => this.GoToTabAtIndexRequested?.Invoke(this, index));
            }
            return;
        }

        // #1310: Ctrl+W is intentionally NOT fanned out as a legacy typed event. The generic
        // AcceleratorKeyPressed subscriber already forwards Ctrl+W to the top-level
        // CloseActiveTabCommand KeyBinding via BrowserAcceleratorBehavior; posting an
        // additional CloseTabRequested here would double-close (one tab per path, in two
        // different dock regions).
    }

    /// <summary>Reverse of <see cref="VirtualKeyMap.ToKey"/> restricted to the subset used by the legacy typed fan-out.</summary>
    private static int ToVirtualKey(Avalonia.Input.Key key) => key switch
    {
        Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt => VkMenu,
        Avalonia.Input.Key.W => VkW,
        >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => VkDigit0 + (int)(key - Avalonia.Input.Key.D0),
        _ => 0,
    };
}

