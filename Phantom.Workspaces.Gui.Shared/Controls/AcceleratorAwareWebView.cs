using System;
using System.Reflection;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Abstract base class that sits between <see cref="NativeWebView"/> and concrete web-view
/// controls. Subscribes to <see cref="NativeWebView.AdapterCreated"/> once the WebView2
/// platform adapter is ready, wires up the COM-level <c>AcceleratorKeyPressed</c> hook, and
/// exposes events that subclasses (and their owners) can subscribe to.
/// </summary>
public abstract class AcceleratorAwareWebView : NativeWebView, IBrowserAcceleratorSource
{
    // Kept alive to prevent GC while the COM callback is registered.
    private AcceleratorKeyPressedHandler? acceleratorKeyHandler;

    protected AcceleratorAwareWebView()
    {
        this.AdapterCreated += this.OnAdapterCreated;
    }

    /// <summary>Raised on the UI thread when Alt is pressed (true) or released (false) inside this WebView.</summary>
    public event EventHandler<bool>? AltKeyStateChanged;

    /// <summary>Raised on the UI thread when Alt+N is pressed inside this WebView. Argument is 0-based tab index.</summary>
    public event EventHandler<int>? GoToTabAtIndexRequested;

    /// <summary>Raised on the UI thread when Alt+Shift+N is pressed inside this WebView. Argument is 0-based workspace pane index.</summary>
    public event EventHandler<int>? GoToWorkspacePaneAtIndexRequested;

    /// <summary>Raised on the UI thread when Ctrl+W is pressed inside this WebView.</summary>
    public event EventHandler? CloseTabRequested;

    /// <inheritdoc/>
    public event EventHandler<AcceleratorKeyEventArgs>? AcceleratorKeyPressed;

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        if (OperatingSystem.IsWindows())
            this.SubscribeAcceleratorKeyPressed();
    }

    [SupportedOSPlatform("windows")]
    private void SubscribeAcceleratorKeyPressed()
    {
        try
        {
            // TryGetAdapter() is internal to Avalonia.Controls.WebView — call it via reflection.
            var tryGetAdapterMethod = typeof(NativeWebView).GetMethod(
                "TryGetAdapter",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var adapter = tryGetAdapterMethod?.Invoke(this, null);

            this.acceleratorKeyHandler = WebView2AcceleratorInterop.Subscribe(
                adapter,
                onAltKeyState: held => Dispatcher.UIThread.Post(
                    () => this.AltKeyStateChanged?.Invoke(this, held)),
                onGoToTab: idx => Dispatcher.UIThread.Post(
                    () => this.GoToTabAtIndexRequested?.Invoke(this, idx)),
                onCloseTab: () => Dispatcher.UIThread.Post(
                    () => this.CloseTabRequested?.Invoke(this, EventArgs.Empty)),
                onGoToWorkspacePane: idx => Dispatcher.UIThread.Post(
                    () => this.GoToWorkspacePaneAtIndexRequested?.Invoke(this, idx)),
                // Generic listener must run synchronously so listeners can set Handled before the
                // COM handler returns to WebView2. The AcceleratorKeyPressed COM event is already
                // raised on the UI thread, so no dispatch is required.
                onAcceleratorKeyPressed: args => this.AcceleratorKeyPressed?.Invoke(this, args));
        }
        catch (Exception)
        {
        }
    }
}
