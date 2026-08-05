using System;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Exposes the WebView2 accelerator-key stream to hosts (e.g. <see cref="AcceleratorAwareWebView"/>
/// in production, or a test stub in the headless harness). Hosts subscribe to
/// <see cref="AcceleratorKeyPressed"/> to forward keystrokes to the Avalonia key-binding pipeline
/// even when focus is inside the native WebView.
/// </summary>
public interface IBrowserAcceleratorSource
{
    /// <summary>
    /// Raised on the UI thread when the browser receives an accelerator key. Listeners may set
    /// <see cref="AcceleratorKeyEventArgs.Handled"/> to true to prevent the WebView from also
    /// processing the key.
    /// </summary>
    event EventHandler<AcceleratorKeyEventArgs>? AcceleratorKeyPressed;
}
